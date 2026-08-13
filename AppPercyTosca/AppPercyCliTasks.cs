using AppPercyTosca.Core;
using Tricentis.Automation.AutomationInstructions.TestActions;
using Tricentis.Automation.Engines;
using Tricentis.Automation.Engines.SpecialExecutionTasks;
using Tricentis.Automation.Engines.SpecialExecutionTasks.Attributes;

namespace AppPercyTosca
{
    /// Starts the Percy CLI and waits until it is serving, so a sheet does not depend on someone having
    /// run `percy app:exec:start` in a terminal first. Forgetting to is not a loud failure: snapshots
    /// are swallowed, every step passes, and the build is empty.
    ///
    /// Put this once at the start of a test case, and AppPercyStopCli at the end.
    [SpecialExecutionTaskName("AppPercyStartCli")]
    public class AppPercyStartCli : SpecialExecutionTask
    {
        public AppPercyStartCli(Tricentis.Automation.Creation.Validator validator) : base(validator)
        {
            Utils.LogSink = ToscaLog.Write;
        }

        public override ActionResult Execute(ISpecialExecutionTaskTestAction testAction)
        {
            ToscaOptions.ParameterReader read = name => CliParameters.Read(testAction, name);
            ToscaOptions.ApplyEnvironment(read);

            try
            {
                return new PassedActionResult(PercyCliLifecycle.Start(
                    new CliLauncher(read("CliCommand"), CliParameters.Token(read)),
                    CliParameters.Client.Value));
            }
            catch (Exception e)
            {
                // Unlike a snapshot, this one fails the step. A run whose CLI never started produces a
                // green sheet and an empty build, which is the outcome this task exists to prevent.
                string message = Utils.RedactCredentials(e.Message);
                Utils.Log($"Could not start the Percy CLI: {message}");
                return new UnknownFailedActionResult($"Could not start the Percy CLI: {message}");
            }
        }
    }

    /// Stops the Percy CLI, which is what finalizes the build, and reports the build link so it lands in
    /// the Tosca result rather than only in a terminal nobody kept.
    [SpecialExecutionTaskName("AppPercyStopCli")]
    public class AppPercyStopCli : SpecialExecutionTask
    {
        public AppPercyStopCli(Tricentis.Automation.Creation.Validator validator) : base(validator)
        {
            Utils.LogSink = ToscaLog.Write;
        }

        public override ActionResult Execute(ISpecialExecutionTaskTestAction testAction)
        {
            ToscaOptions.ParameterReader read = name => CliParameters.Read(testAction, name);
            ToscaOptions.ApplyEnvironment(read);

            try
            {
                return new PassedActionResult(PercyCliLifecycle.Stop(
                    new CliLauncher(read("CliCommand"), CliParameters.Token(read))));
            }
            catch (Exception e)
            {
                // Passes rather than fails: the snapshots are already uploaded, and a stop that could
                // not be issued leaves a build Percy finalizes on its own timeout. Failing the last
                // step of an otherwise good run would be the more misleading answer.
                string message = Utils.RedactCredentials(e.Message);
                Utils.Log($"Could not stop the Percy CLI: {message}", "warn");
                return new PassedActionResult($"Could not stop the Percy CLI: {message}");
            }
        }
    }

    /// Shared plumbing for the two CLI tasks.
    internal static class CliParameters
    {
        /// One connection for both tasks, matching AppPercyScreenshot's client so the healthcheck memo
        /// is shared: the start task's "is one already running" and a later snapshot's are the same
        /// question.
        internal static readonly Lazy<PercyClient> Client = new Lazy<PercyClient>(() =>
            new PercyClient(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }));

        internal static string? Read(ISpecialExecutionTaskTestAction testAction, string name)
        {
            try
            {
                string? value = testAction.GetParameterAsInputValue(name, true)?.Value?.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception)
            {
                // A row Tosca cannot render means unset, same as AppPercyScreenshot treats it.
                return null;
            }
        }

        /// The project token, from the module first and the environment second. Never logged, and passed
        /// to the child through its environment rather than its arguments.
        internal static string? Token(ToscaOptions.ParameterReader read) =>
            read("PercyToken") ?? Env.Read("PERCY_TOKEN");
    }
}
