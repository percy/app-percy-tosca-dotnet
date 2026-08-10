using AppPercyTosca.Core;
using Tricentis.Automation.AutomationInstructions.TestActions;
using Tricentis.Automation.Creation.Attributes;
using Tricentis.Automation.Engines;
using Tricentis.Automation.Engines.SpecialExecutionTasks;
using Tricentis.Automation.Engines.SpecialExecutionTasks.Attributes;

[assembly: EngineId("Percy")]

namespace AppPercyTosca
{
    /// Takes one App Percy screenshot of the mobile device under test.
    ///
    /// This class and its two neighbours are the only code that touches Tosca; everything with a
    /// decision in it lives in AppPercyTosca.Core, which CI can test. Anything here is unreachable by
    /// any test, since the Tricentis assemblies exist only where Tosca is installed.
    ///
    /// No <c>[SupportedTechnical]</c>: that tags an adapter with a technical, and a special execution
    /// task is never handed one.
    [SpecialExecutionTaskName("AppPercyScreenshot")]
    public class AppPercyScreenshot : SpecialExecutionTask
    {
        /// One CLI connection per Commander process, with a memoized healthcheck — otherwise a sheet
        /// with fifty steps performs fifty healthchecks.
        private static readonly Lazy<PercyClient> Client = new Lazy<PercyClient>(() =>
        {
            HttpClient http = new HttpClient
            {
                // Generous on purpose: /percy/comparison uploads a screenshot.
                Timeout = TimeSpan.FromMinutes(10)
            };
            return new PercyClient(http);
        });

        /// A second connection for log forwarding, on a short timeout. It cannot share the client
        /// above: every log line is a blocking POST, so a CLI that accepts a connection then stops
        /// answering would stall each line for ten minutes — and those lines are usually the ones
        /// reporting that Percy is unreachable. The file copy in <see cref="ToscaLog"/> is the record
        /// that survives if five seconds is not enough.
        internal static PercyClient LogClient => Log.Value;

        private static readonly Lazy<PercyClient> Log = new Lazy<PercyClient>(() =>
            new PercyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }));

        /// The device's automation server, which may be a remote hub serving a large screen.
        ///
        /// The timeout here is only a backstop. HttpClient.Timeout caps every request through this
        /// client, so a value below the longest capture would silently override the per-request budgets
        /// in CaptureBudget — which is exactly how iOS full page came to fail at two minutes. Each
        /// request sets its own deadline; this one exists so that a future call site added without one
        /// cannot hang forever.
        private static readonly Lazy<HttpClient> DeviceHttp = new Lazy<HttpClient>(() =>
            new HttpClient { Timeout = CaptureBudget.FullPage + TimeSpan.FromMinutes(1) });

        public AppPercyScreenshot(Tricentis.Automation.Creation.Validator validator) : base(validator)
        {
            // Commander has no console attached, so without a sink every log line goes nowhere.
            Utils.LogSink = ToscaLog.Write;
            Env.ToscaVersion ??= DetectToscaVersion();
        }

        /// Reported to Percy as environmentInfo. Read off the loaded assembly because no API returns
        /// it, and a wrong answer here is cosmetic.
        private static string? DetectToscaVersion()
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => (a.GetName().Name ?? "")
                        .StartsWith("Tricentis.AutomationBase", StringComparison.OrdinalIgnoreCase))
                    ?.GetName().Version?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override ActionResult Execute(ISpecialExecutionTaskTestAction testAction)
        {
            ToscaOptions.ParameterReader read = name => Parameter(testAction, name);

            // Before the first log line, and before the SnapshotName check that can log and return:
            // LogLevel and LogFile decide whether and where any of it is recorded. Tosca cannot set
            // environment variables for its own process, so these rows are the only way in.
            ToscaOptions.ApplyEnvironment(read);

            string? snapshotName = read("SnapshotName");
            if (string.IsNullOrWhiteSpace(snapshotName))
            {
                Utils.Log("SnapshotName cannot be empty!");
                return new UnknownFailedActionResult("SnapshotName cannot be empty!");
            }

            Utils.Log($"Starting Percy screenshot, {snapshotName}");

            try
            {
                ScreenshotOptions options = ToscaOptions.Build(read);

                // Takes no test action: its parameters and buffers come from Tosca's own singletons.
                ToscaEnvironment tosca = new ToscaEnvironment();
                ToscaMobileDriver driver = new ToscaMobileDriver(
                    tosca,
                    SessionKey(testAction),
                    read("SessionId"),
                    (server, sessionId) => new WebDriverSession(DeviceHttp.Value, server, sessionId));

                // Passes whether or not a snapshot was recorded, but never claims one that was not:
                // see SnapshotOutcome.
                return new PassedActionResult(Screenshot(driver, snapshotName!, options));
            }
            catch (Exception e)
            {
                // Only reached under percy.ignoreErrors=false; the Core swallows everything else.
                string message = Utils.RedactCredentials(e.Message);
                Utils.Log($"Percy snapshot {snapshotName} failed: {message}");
                return new UnknownFailedActionResult($"Percy snapshot failed: {message}");
            }
        }

        /// Returns what the step should report; every outcome but a throw leaves it passing.
        private static string Screenshot(
            ToscaMobileDriver driver,
            string snapshotName,
            ScreenshotOptions options)
        {
            // Must run before the mode check below: without it the first step of a Commander session
            // reads SessionType as null, takes the App Percy path against an automate-mode CLI, has
            // the comparison rejected and swallowed, and reports a passing step with nothing in the
            // build — while every later step works, which reads as a flake. Memoized, so this is free
            // after the first call.
            if (!Client.Value.Healthcheck())
            {
                return SnapshotOutcome.PercyNotRunning;
            }

            if (Env.IsAutomateSession)
            {
                // Said here because the alternative is the CLI rejecting the comparison with an error
                // that never mentions how it was started.
                Utils.Log("The Percy CLI is running in Percy on Automate mode, which this SDK does not " +
                    "support. Restart it for App Percy: use an App project token (not one starting " +
                    "with \"auto_\") and `percy app:exec:start`.");
                return "Percy CLI is in Percy on Automate mode; App Percy snapshot skipped";
            }

            return SnapshotOutcome.Describe(
                AppPercyFor(driver).Screenshot(snapshotName, options), snapshotName);
        }

        // A fresh façade per step: the Core's caches are per-session and this driver is per-step, so
        // there is nothing to carry across. The expensive parts are static above.
        private static AppPercy AppPercyFor(ToscaMobileDriver driver) =>
            new AppPercy(driver, Client.Value);

        /// Cache key for when no session id is available. Stable within a step and deliberately not
        /// across them, since a stale entry would report the previous device's dimensions.
        private static string SessionKey(ISpecialExecutionTaskTestAction testAction) =>
            "tosca-" + testAction.GetHashCode();

        /// Reads one module parameter, or null when absent or blank. The <c>true</c> marks it optional,
        /// which is what lets a step carry only the rows it needs.
        private static string? Parameter(ISpecialExecutionTaskTestAction testAction, string name)
        {
            try
            {
                string? value = testAction.GetParameterAsInputValue(name, true)?.Value?.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception e)
            {
                // A row Tosca cannot render must not take the snapshot down; unset means the default.
                Utils.Log($"Could not read the {name} parameter: {e.Message}", "debug");
                return null;
            }
        }
    }
}
