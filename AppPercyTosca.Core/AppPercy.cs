using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// Takes one screenshot of the device and posts it to the CLI.
    public class AppPercy
    {
        private readonly IMobileDriver _driver;
        private readonly PercyClient _client;
        private readonly PercyOptions _percyOptions;
        private readonly Cache<string, object?> _sessionCache = new Cache<string, object?>();
        private readonly bool _isPercyEnabled;

        public AppPercy(IMobileDriver driver, PercyClient client)
        {
            _driver = driver;
            _client = client;
            _percyOptions = new PercyOptions(driver, _sessionCache);
            _isPercyEnabled = client.Healthcheck();
        }

        /// The CLI's whole response, or null when Percy is off, disabled, or the capture failed with
        /// errors being ignored. Throws only under percy.ignoreErrors=false.
        ///
        /// The whole response, not its `data` member: a successful reply is `{success, link}` and often
        /// carries no `data`, so treating that as proof of success reports working snapshots as lost.
        public JsonElement? Screenshot(string name, ScreenshotOptions options)
        {
            if (!_isPercyEnabled || !_percyOptions.PercyEnabled()) return null;

            bool ignoreErrors = _percyOptions.IgnoreErrors();
            try
            {
                AppAutomate provider = new AppAutomate(_driver, _client, _sessionCache);
                return provider.Screenshot(name, options);
            }
            catch (Exception e)
            {
                _client.PostFailedEvent(Utils.RedactCredentials(e.Message));

                if (e is PercyException)
                {
                    Utils.Log("The device session could not serve this request; see below.", "warn");
                }
                // Named here because this method otherwise swallows, and PostFailedEvent redacts what
                // it forwards — so without the type there is no full copy of it anywhere.
                Utils.Log($"Error taking screenshot {name} - {e.GetType().Name}: {e.Message}");
                Utils.Log(e.ToString(), "debug");

                if (!ignoreErrors)
                {
                    throw new PercyException($"Error taking screenshot {name}", e);
                }
                return null;
            }
        }

        /// Drops this session's cached capability and metadata reads.
        public void ClearSessionCache() => _sessionCache.Clear();
    }
}
