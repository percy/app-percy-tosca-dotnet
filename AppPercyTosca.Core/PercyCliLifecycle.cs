using System.Text.RegularExpressions;

namespace AppPercyTosca.Core
{
    /// Starts and stops the Percy CLI from inside Tosca, so a run does not depend on someone having
    /// typed `percy app:exec:start` in a terminal first — which on a Tosca machine is both easy to
    /// forget and hard to notice, because a missing CLI produces passing steps and an empty build.
    public static class PercyCliLifecycle
    {
        /// What `app:exec:start` prints once it is serving. Readiness is confirmed by healthcheck as
        /// well, because a log line is the CLI's presentation rather than its contract.
        public const string ReadyLine = "Percy has started!";

        /// The CLI downloads a browser on first run, so the first start on a fresh machine is much
        /// slower than the rest.
        public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

        /// How long to wait for any single line before concluding nothing more is coming.
        public static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(30);

        public const string Started = "Percy CLI started";
        public const string AlreadyRunning = "Percy CLI was already running; leaving it alone";
        public const string Stopped = "Percy CLI stopped";

        /// Matches the build URL the CLI prints when it finalizes, e.g.
        /// `[percy] Finalized build #14: https://percy.io/<org>/<project>/builds/123`.
        private static readonly Regex BuildLink =
            new Regex(@"https://percy\.io/\S+", RegexOptions.Compiled);

        /// Brings the CLI up, or reports why not. The started process is deliberately not returned:
        /// nothing needs to hold it, and Stop finds it by asking the CLI rather than by handle, which
        /// also works when the two steps run in different Commander processes.
        public static string Start(ICliLauncher launcher, PercyClient client)
        {
            if (launcher == null) throw new ArgumentNullException(nameof(launcher));
            if (client == null) throw new ArgumentNullException(nameof(client));

            // Starting a second CLI on the same port fails with an error about the port rather than
            // about the mistake, and would leave the first one serving a different build.
            if (client.Healthcheck())
            {
                Utils.Log(AlreadyRunning);
                return AlreadyRunning;
            }

            ICliProcess process = launcher.Start("app:exec:start");
            try
            {
                if (WaitForReady(process))
                {
                    // The log line said so; the healthcheck proves it. Env.PercyBuildId and
                    // PercyBuildUrl are populated as a side effect, which is what lets a later Stop
                    // report the build even if the CLI's own output does not.
                    client.ResetHealthcheck();
                    if (client.Healthcheck())
                    {
                        Utils.Log($"{Started}: {Env.PercyBuildUrl ?? "build url not reported yet"}");
                        return Started;
                    }

                    Utils.Log("The Percy CLI printed that it had started but is not answering its " +
                        "healthcheck. Check PERCY_CLI_API and whether something else holds the port.",
                        "warn");
                }

                process.Kill();
                return "Percy CLI did not start; see the log";
            }
            catch (Exception)
            {
                // A launcher that throws mid-start leaves a process nobody will stop otherwise.
                process.Kill();
                throw;
            }
        }

        /// Reads until the CLI says it is ready, gives up, or dies. Every line is logged: when a start
        /// fails, the CLI's own output is the only explanation there is.
        private static bool WaitForReady(ICliProcess process)
        {
            DateTime deadline = Clock() + StartTimeout;

            while (Clock() < deadline)
            {
                string? line = process.ReadLine(LineTimeout);
                if (line == null)
                {
                    // Nothing more will arrive. An exited process is a failed start; a live one that
                    // has gone quiet without saying it started is the same outcome from here.
                    if (process.HasExited)
                    {
                        Utils.Log($"The Percy CLI exited with code {process.ExitCode} instead of " +
                            "starting. Check that @percy/cli is installed and PERCY_TOKEN is set for " +
                            "an App project.");
                    }
                    return false;
                }

                Utils.Log($"percy: {line}", "debug");
                if (line.Contains(ReadyLine, StringComparison.OrdinalIgnoreCase)) return true;
            }

            Utils.Log($"The Percy CLI did not report \"{ReadyLine}\" within " +
                $"{StartTimeout.TotalSeconds:0}s.", "warn");
            return false;
        }

        /// Stops the CLI and reports the build it finalized. The link is what a Tosca step should carry:
        /// it is the one artefact of a Percy run someone wants from a test report.
        public static string Stop(ICliLauncher launcher)
        {
            if (launcher == null) throw new ArgumentNullException(nameof(launcher));

            using ICliProcess process = launcher.Start("app:exec:stop");

            string? link = null;
            for (string? line = process.ReadLine(LineTimeout);
                 line != null;
                 line = process.ReadLine(LineTimeout))
            {
                Utils.Log($"percy: {line}");
                Match match = BuildLink.Match(line);
                // Last wins: the CLI prints the build URL when it starts finalizing and again when it
                // is done, and the later one is the finalized build.
                if (match.Success) link = match.Value.TrimEnd('.', ',', ')');
            }

            // The CLI's own output is preferred, but a stop that printed nothing useful can still name
            // the build, because the healthcheck recorded it when the SDK first connected.
            link ??= Env.PercyBuildUrl;

            string outcome = string.IsNullOrWhiteSpace(link) ? Stopped : $"{Stopped}: {link}";
            Utils.Log(outcome);
            return outcome;
        }

        /// Replaceable so a test does not wait three minutes to see the timeout branch.
        internal static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    }
}
