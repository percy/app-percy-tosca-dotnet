using System.Text.RegularExpressions;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Logging and credential redaction. Tosca Commander has no console attached to write to, so
    /// log output is routed through a sink the shim installs (which forwards to the Percy CLI's
    /// /percy/log endpoint and to a file); the default sink writes to stdout for tests and tools.
    /// </summary>
    public static class Utils
    {
        /// <summary>
        /// Receives (message, level). Replaced by the Tosca shim; assigning null restores the
        /// stdout default.
        /// </summary>
        public static Action<string, string>? LogSink { get; set; }

        // Appium/Selenium exception text embeds the command-executor URI, commonly supplied as
        // https://user:accesskey@hub-cloud.browserstack.com/wd/hub. Applied inside Emit so every
        // call site is covered.
        // Keyed on the scheme because region logging emits locators ("xpath://a[@id='x']") that
        // carry `://` and `@` but never an http/ws scheme. Matching on userinfo content instead
        // fails open: any unanticipated character in a generated password leaks the whole URL.
        // Excluding `/` keeps a match out of the path.
        private static readonly Regex UrlUserInfo =
            new Regex(@"\b(https?|wss?)://[^\s@/]+@", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CredentialQuery =
            new Regex(@"([?&](?:access[_-]?key|auth[_-]?token|token|password|secret)=)[^&\s""']+",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string RedactCredentials(string? message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? "";
            message = UrlUserInfo.Replace(message, "$1://***@");
            return CredentialQuery.Replace(message, "$1***");
        }

        public static void Log(string message, string logLevel = "info")
        {
            // Debug lines are dropped unless asked for; every other level is always emitted.
            if (logLevel == "debug" && !Env.Debug()) return;
            Emit(message, logLevel);
        }

        private static void Emit(string message, string level)
        {
            string redacted = RedactCredentials(message);
            Action<string, string>? sink = LogSink;
            if (sink != null)
            {
                try
                {
                    sink(redacted, level);
                    return;
                }
                catch (Exception)
                {
                    // A failing sink must not take the snapshot down with it, and must not
                    // silently swallow the line either — fall through to stdout.
                }
            }
            string label = level == "info" ? "percy" : "percy:tosca";
            string color = level switch { "warn" => "93m", "debug" => "91m", _ => "39m" };
            Console.WriteLine($"[\u001b[35m{label}\u001b[{color}] {redacted}");
        }
    }
}
