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

        public static bool ForceFullPage() => GetFlag("FORCE_FULL_PAGE");

        public static bool DisableRemoteUploads() => GetFlag("PERCY_DISABLE_REMOTE_UPLOADS");

        public static bool EnablePercyDev() => GetFlag("PERCY_ENABLE_DEV");

        public static bool Debug() =>
            string.Equals(Environment.GetEnvironmentVariable("PERCY_LOGLEVEL"), "debug",
                StringComparison.OrdinalIgnoreCase);

        public static string CliApi() =>
            Environment.GetEnvironmentVariable("PERCY_CLI_API") ?? "http://localhost:5338";

        /// <summary>Where tiles are written for the CLI to read.</summary>
        public static string TempDir()
        {
            string? dir = Environment.GetEnvironmentVariable("PERCY_TMP_DIR");
            return string.IsNullOrWhiteSpace(dir) ? Path.GetTempPath() : dir;
        }

        /// <summary>Domain fragment that marks a host as BrowserStack App Automate.</summary>
        public static string AutomateDomain() =>
            Environment.GetEnvironmentVariable("AA_DOMAIN") ?? "browserstack";

        private static bool GetFlag(string name) =>
            string.Equals(Environment.GetEnvironmentVariable(name), "true",
                StringComparison.OrdinalIgnoreCase);

        /// <summary>Clears the per-run state. Called between test runs and by tests.</summary>
        public static void Reset()
        {
            PercyBuildId = null;
            PercyBuildUrl = null;
            SessionType = null;
            ToscaVersion = null;
        }
    }
}
