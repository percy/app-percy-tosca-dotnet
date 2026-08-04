using AppPercyTosca.Core;
using Tricentis.Automation.AutomationInstructions.TestActions;
using Tricentis.Automation.Creation.Attributes;
using Tricentis.Automation.Engines;
using Tricentis.Automation.Engines.SpecialExecutionTasks;
using Tricentis.Automation.Engines.SpecialExecutionTasks.Attributes;

[assembly: EngineId("Percy")]

namespace AppPercyTosca
{
    /// <summary>
    /// The PercyScreenshot special execution task: takes one App Percy screenshot of the mobile
    /// device under test.
    ///
    /// This class and its two neighbours are the only code that touches Tosca. Everything with a
    /// decision in it — parsing parameters, resolving device metadata, choosing a capture path,
    /// talking to the Percy CLI — lives in AppPercyTosca.Core, which has no Tricentis dependency and
    /// is unit-tested on CI. The Tricentis assemblies only exist on a machine with Tosca installed,
    /// so anything here is code no test can reach.
    ///
    /// No <c>[SupportedTechnical]</c> attribute: that attribute tags an adapter with the technical
    /// it can be built for, and a special execution task is never handed a technical. Tosca's own
    /// task examples carry only the two attributes used here.
    /// </summary>
    [SpecialExecutionTaskName("PercyScreenshot")]
    public class AppPercyScreenshot : SpecialExecutionTask
    {
        /// <summary>
        /// One CLI connection for the Tosca Commander process. The healthcheck behind it is
        /// memoized, which is what keeps a sheet with fifty PercyScreenshot steps from performing
        /// fifty healthchecks.
        /// </summary>
        private static readonly Lazy<PercyClient> Client = new Lazy<PercyClient>(() =>
        {
            HttpClient http = new HttpClient
            {
                // Generous on purpose: /percy/comparison uploads a screenshot, and
                // /percy/automateScreenshot waits for the CLI to drive the remote session.
                Timeout = TimeSpan.FromMinutes(10)
            };
            return new PercyClient(http);
        });

        /// <summary>The shared CLI connection, used by <see cref="ToscaLog"/> to forward log lines.</summary>
        internal static PercyClient CliClient => Client.Value;

        public AppPercyScreenshot(Tricentis.Automation.Creation.Validator validator) : base(validator)
        {
            // Tosca Commander is a desktop process with no console attached, so without a sink every
            // log line — including the ones explaining a degraded snapshot — would go nowhere.
            Utils.LogSink = ToscaLog.Write;
            Env.ToscaVersion ??= DetectToscaVersion();
        }

        /// <summary>
        /// The Tosca version, reported to Percy as part of environmentInfo so a build can be
        /// attributed to a Tosca release. Read from the loaded Tricentis assembly rather than asked
        /// for, since there is no API that returns it and a wrong answer here is cosmetic.
        /// </summary>
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
            string? snapshotName = Parameter(testAction, "SnapshotName");
            if (string.IsNullOrWhiteSpace(snapshotName))
            {
                Utils.Log("SnapshotName cannot be empty!");
                return new UnknownFailedActionResult("SnapshotName cannot be empty!");
            }

            Utils.Log($"Starting Percy screenshot, {snapshotName}");

            try
            {
                ToscaOptions.ParameterReader read = name => Parameter(testAction, name);

                // Built once and shared: the driver reads the device details out of it to fill gaps
                // in what Tosca reports, and the capture flow needs the same values. Building it
                // twice would re-log every parse warning.
                ScreenshotOptions options = ToscaOptions.Build(read);

                ToscaEnvironment tosca = new ToscaEnvironment(testAction);
                ToscaMobileDriver driver = new ToscaMobileDriver(
                    tosca,
                    options,
                    Parameter(testAction, "SessionIdBuffer"),
                    SessionKey(testAction));

                if (ToscaOptions.ParseBool(Parameter(testAction, "Diagnose"), "Diagnose") == true)
                {
                    Utils.Log("Percy session diagnostics:" + Environment.NewLine + Describe(driver));
                }

                Screenshot(driver, snapshotName!, options, read);
                return new PassedActionResult("Snapshot Taken!");
            }
            catch (Exception e)
            {
                // Only reached when the session asked for errors not to be ignored (percyOptions
                // ignoreErrors=false, or a missing session id on the Percy on Automate path); the
                // Core swallows everything else so a visual check cannot fail a passing sheet.
                string message = Utils.RedactCredentials(e.Message);
                Utils.Log($"Percy snapshot {snapshotName} failed: {message}");
                return new UnknownFailedActionResult($"Percy snapshot failed: {message}");
            }
        }

        /// <summary>
        /// Dispatches to the flow the CLI's session type calls for. Percy on Automate hands the
        /// session to the CLI to capture remotely; App Percy captures here and uploads a tile.
        /// </summary>
        private static void Screenshot(
            ToscaMobileDriver driver,
            string snapshotName,
            ScreenshotOptions options,
            ToscaOptions.ParameterReader read)
        {
            // The healthcheck is what tells us the session type, and it has to happen before the
            // branch below rather than inside the façade constructors underneath it. Without this,
            // the very first PercyScreenshot step of a Tosca Commander session reads SessionType as
            // null, takes the App Percy path against an automate-mode CLI, has the comparison
            // rejected (and swallowed), and reports a passing step with no snapshot in the build —
            // while every later step behaves correctly, which reads as a flake.
            //
            // Memoized inside PercyClient, so the later Healthcheck() calls are free.
            if (!Client.Value.Healthcheck())
            {
                // Percy is not running or is too old; it has already said so. Nothing to capture,
                // and no reason to fail the step over it.
                return;
            }

            if (Env.IsAutomateSession)
            {
                if (!driver.HasRealSessionId)
                {
                    // Refused here rather than posted, because the CLI's failure to attach to a
                    // made-up session id says nothing about the buffer that is actually missing.
                    throw new PercyException(
                        "Percy on Automate needs the Appium session id, which was not found in " +
                        $"buffer '{ToscaMobileDriver.DefaultSessionIdBuffer}'. Add the " +
                        "'Get Appium Session Id' standard module to the test case before this step " +
                        "and have it write to that buffer, or name your own buffer in the " +
                        "SessionIdBuffer parameter.");
                }
                // Rebuilt from the parameters rather than converted from `options`: the CLI owns this
                // schema, and BuildAutomateOptions omits anything the step left unset so a project
                // default is deferred to rather than overridden with a null.
                OnAutomate(driver).Screenshot(snapshotName, ToscaOptions.BuildAutomateOptions(read));
                return;
            }
            AppPercyFor(driver).Screenshot(snapshotName, options);
        }

        // A fresh façade per step. The Core's caches are per-session and this driver is per-step, so
        // there is nothing to carry across; the CLI connection and its healthcheck — the only
        // genuinely expensive parts — are static above.
        private static AppPercy AppPercyFor(ToscaMobileDriver driver) =>
            new AppPercy(driver, Client.Value);

        private static PercyOnAutomate OnAutomate(ToscaMobileDriver driver) =>
            new PercyOnAutomate(driver, Client.Value);

        /// <summary>
        /// What the SDK could and could not read, for the Diagnose parameter. This is the first stop
        /// when a snapshot comes out wrong — nearly every such case is a missing test configuration
        /// parameter or an unset session-id buffer, and both show up plainly here.
        /// </summary>
        private static string Describe(ToscaMobileDriver driver)
        {
            IEnumerable<string> lines = new[]
            {
                // RedactCredentials turns null into "", so the fallback has to be chosen before
                // redacting — otherwise a missing AppiumServer prints an empty value, and this dump
                // exists to make exactly that visible.
                $"host (AppiumServer): {(string.IsNullOrWhiteSpace(driver.Host) ? "(not found)" : Utils.RedactCredentials(driver.Host))}",
                $"appium session id:   {(driver.HasRealSessionId ? driver.SessionId : "(not found)")}",
                $"platform:            {driver.PlatformName ?? "(not found)"}",
                $"session type:        {Env.SessionType ?? "(app percy)"}",
                $"can execute scripts: {driver.CanExecuteScript}",
                "capabilities:"
            }.Concat(driver.Capabilities.Count == 0
                ? new[] { "  (none found — set DeviceName, OsName and OsVersion on the module)" }
                : driver.Capabilities.Select(c => $"  {c.Key} = {Utils.RedactCredentials(c.Value?.ToString())}"));

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Cache key used when no Appium session id is available. Derived from the test action so it
        /// is stable within a step; it deliberately does not persist across steps, since a stale
        /// entry would report the previous device's dimensions.
        /// </summary>
        private static string SessionKey(ISpecialExecutionTaskTestAction testAction) =>
            "tosca-" + testAction.GetHashCode();

        /// <summary>
        /// Reads one module parameter, or null when the row is absent or blank.
        ///
        /// The <c>true</c> argument tells Tosca the parameter is optional, which is what makes every
        /// parameter besides SnapshotName optional — a user should not have to add twenty rows to
        /// take one screenshot.
        /// </summary>
        private static string? Parameter(ISpecialExecutionTaskTestAction testAction, string name)
        {
            try
            {
                string? value = testAction.GetParameterAsInputValue(name, true)?.Value?.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception e)
            {
                // A row Tosca cannot render as an input value must not take the snapshot down;
                // treating it as unset falls back to the documented default.
                Utils.Log($"Could not read the {name} parameter: {e.Message}", "debug");
                return null;
            }
        }
    }
}
