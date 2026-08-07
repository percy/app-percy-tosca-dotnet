using System.Collections.Concurrent;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Ambient state shared across a run: the Percy build the CLI reported at healthcheck, the
    /// session type it resolved, and the client/environment strings stamped onto every request.
    /// </summary>
    public static class Env
    {
        /// <summary>Reported to Percy as clientInfo. Keep in step with Version in AppPercyTosca.csproj.</summary>
        public const string SdkVersion = "1.0.0";

        /// <summary>
        /// What the CLI reports for <c>type</c> when started for Percy on Automate. Used to warn, not
        /// to choose a path: the CLI would reject the comparison with an error that never mentions how
        /// it was started.
        /// </summary>
        public const string AutomateSessionType = "automate";

        public static string? PercyBuildId { get; set; }
        public static string? PercyBuildUrl { get; set; }
        public static string? SessionType { get; set; }

        /// <summary>Set by the shim, so a build can be attributed to a Tosca release.</summary>
        public static string? ToscaVersion { get; set; }

        public static string ClientInfo => $"percy-app-tosca/{SdkVersion}";

        public static string EnvironmentInfo =>
            string.IsNullOrWhiteSpace(ToscaVersion) ? "tosca" : $"tosca/{ToscaVersion}";

        /// <summary>True when the CLI is in a mode this SDK does not support.</summary>
        public static bool IsAutomateSession =>
            string.Equals(SessionType, AutomateSessionType, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Values supplied on the module in place of environment variables.
        ///
        /// Tosca cannot set environment variables for its own process, so on Tosca every knob below is
        /// otherwise unreachable — including the log level, which is what you need before you can see
        /// why anything else went wrong. Concurrent because Tosca can run steps in parallel.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> Supplied =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Supplies one variable for this run, or clears it when the value is blank. A supplied value
        /// wins over the process environment: it is the more specific statement of intent, and on Tosca
        /// it is usually the only one available.
        /// </summary>
        public static void Supply(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            if (string.IsNullOrWhiteSpace(value)) Supplied.TryRemove(name, out _);
            else Supplied[name] = value.Trim();
        }

        /// <summary>What is in force for a variable, from the module first and the environment second.</summary>
        public static string? Read(string name) =>
            Supplied.TryGetValue(name, out string? supplied)
                ? supplied
                : Environment.GetEnvironmentVariable(name);

        public static bool ForceFullPage() => GetFlag("FORCE_FULL_PAGE");

        public static bool DisableRemoteUploads() => GetFlag("PERCY_DISABLE_REMOTE_UPLOADS");

        public static bool EnablePercyDev() => GetFlag("PERCY_ENABLE_DEV");

        public static bool Debug() =>
            string.Equals(Read("PERCY_LOGLEVEL"), "debug", StringComparison.OrdinalIgnoreCase);

        public static string CliApi() => Read("PERCY_CLI_API") ?? "http://localhost:5338";

        /// <summary>Where tiles are written for the CLI to read.</summary>
        public static string TempDir()
        {
            string? dir = Read("PERCY_TMP_DIR");
            return string.IsNullOrWhiteSpace(dir) ? Path.GetTempPath() : dir;
        }

        /// <summary>
        /// Where the log file copy is written. Read per line rather than once at type load, so a
        /// module parameter can still move it — the assembly-load line is written before any step runs
        /// and therefore always lands at the environment's answer.
        /// </summary>
        public static string LogFile()
        {
            string? custom = Read("PERCY_LOG_FILE");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Path.GetTempPath(), "percy.txt")
                : custom;
        }

        /// <summary>Domain fragment that marks a host as BrowserStack App Automate.</summary>
        public static string AutomateDomain() => Read("AA_DOMAIN") ?? "browserstack";

        private static bool GetFlag(string name) =>
            string.Equals(Read(name), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Clears the per-run state. Called between test runs and by tests.</summary>
        public static void Reset()
        {
            PercyBuildId = null;
            PercyBuildUrl = null;
            SessionType = null;
            ToscaVersion = null;
            Supplied.Clear();
        }
    }
}
