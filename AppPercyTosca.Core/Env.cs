namespace AppPercyTosca.Core
{
    /// <summary>
    /// Ambient state shared across a run: the Percy build the CLI reported at healthcheck, the
    /// session type it resolved, and the client/environment strings stamped onto every request.
    /// </summary>
    public static class Env
    {
        /// <summary>SDK version reported to Percy as clientInfo. Keep in sync with AssemblyInfo.cs.</summary>
        public const string SdkVersion = "1.0.0";

        /// <summary>
        /// Value the CLI reports for <c>type</c> when it was started for Percy on Automate rather than
        /// App Percy. This SDK supports App Percy only, so the value is used to warn rather than to
        /// choose a path — posting an App Percy comparison to an automate-mode CLI is rejected, and the
        /// rejection says nothing about the CLI having been started the wrong way.
        /// </summary>
        public const string AutomateSessionType = "automate";

        public static string? PercyBuildId { get; set; }
        public static string? PercyBuildUrl { get; set; }
        public static string? SessionType { get; set; }

        /// <summary>
        /// Set by the Tosca shim to the Tosca Commander version it is running under, so builds
        /// can be attributed to a Tosca release. Falls back to the bare "tosca" label.
        /// </summary>
        public static string? ToscaVersion { get; set; }

        public static string ClientInfo => $"percy-app-tosca/{SdkVersion}";

        public static string EnvironmentInfo =>
            string.IsNullOrWhiteSpace(ToscaVersion) ? "tosca" : $"tosca/{ToscaVersion}";

        /// <summary>
        /// True when the running CLI was started for Percy on Automate, which this SDK does not
        /// support. Checked so the mismatch can be reported plainly.
        /// </summary>
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

        /// <summary>
        /// Directory screenshot tiles are written to before the CLI reads them. Tosca Commander
        /// runs as a Windows desktop process, so this is typically C:\Users\&lt;user&gt;\AppData\Local\Temp.
        /// </summary>
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
