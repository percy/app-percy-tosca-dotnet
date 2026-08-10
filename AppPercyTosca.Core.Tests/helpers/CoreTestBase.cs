using AppPercyTosca.Core;

namespace AppPercyTosca.Core.Tests
{
    /// Resets the Core's process-wide state around each test and captures log output so tests can
    /// assert on what the SDK told the user, not just on return values — most of the fallback
    /// behaviour here is only observable through a log line.
    public abstract class CoreTestBase : IDisposable
    {
        protected readonly List<(string Message, string Level)> Logs = new List<(string, string)>();

        private static readonly string[] ManagedEnvVars =
        {
            "PERCY_LOGLEVEL", "PERCY_CLI_API", "PERCY_TMP_DIR", "AA_DOMAIN", "PERCY_LOG_FILE",
            "FORCE_FULL_PAGE", "PERCY_DISABLE_REMOTE_UPLOADS", "PERCY_ENABLE_DEV"
        };

        private readonly Dictionary<string, string?> _originalEnv = new Dictionary<string, string?>();

        protected CoreTestBase()
        {
            foreach (string name in ManagedEnvVars)
            {
                _originalEnv[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
            Env.Reset();
            DeviceRegistry.Reset();
            PercyClient.Now = () => DateTime.UtcNow;
            WebDriverSession.Sleep = _ => { };
            Utils.LogSink = (message, level) => Logs.Add((message, level));
        }

        protected void SetEnv(string name, string? value) =>
            Environment.SetEnvironmentVariable(name, value);

        /// True when any captured log line contains <paramref name="fragment"/>.
        protected bool Logged(string fragment) =>
            Logs.Any(entry => entry.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> entry in _originalEnv)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
            Utils.LogSink = null;
            Env.Reset();
            DeviceRegistry.Reset();
            PercyClient.Now = () => DateTime.UtcNow;
            WebDriverSession.Sleep = duration => Thread.Sleep(duration);
        }
    }
}
