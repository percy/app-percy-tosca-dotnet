using System.Text.RegularExpressions;

namespace AppPercyTosca.Core
{
    /// Logging and credential redaction. Commander has no console, so output goes through a sink the
    /// shim installs; the default writes to stdout for tests and tools.
    public static class Utils
    {
        /// Replaced by the shim; null restores the stdout default.
        public static Action<string, string>? LogSink { get; set; }

        // Hub URLs carry credentials as userinfo (https://user:key@hub-cloud.browserstack.com/wd/hub)
        // and turn up in exception text, so redaction is applied inside Emit to cover every call site.
        //
        // Keyed on the scheme, because region logging emits locators ("xpath://a[@id='x']") carrying
        // `://` and `@` but never an http/ws scheme. Keying on userinfo content instead fails open:
        // one unanticipated character in a generated password leaks the whole URL.
        private static readonly Regex UrlUserInfo =
            new Regex(@"\b(https?|wss?)://[^\s@/]+@", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CredentialQuery =
            new Regex(@"([?&](?:access[_-]?key|auth[_-]?token|token|password|secret)=)[^&\s""']+",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // A Percy project token, which the CLI tasks now handle: app_ for App projects, auto_ for
        // Automate. Passed to the child process through its environment rather than its arguments, so
        // this is defence in depth against one reaching a log by another route — an exception message
        // quoting a command line, say.
        private static readonly Regex PercyToken =
            new Regex(@"\b(app|auto)_[0-9a-f]{32,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string RedactCredentials(string? message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? "";
            message = UrlUserInfo.Replace(message, "$1://***@");
            message = PercyToken.Replace(message, "$1_***");
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
