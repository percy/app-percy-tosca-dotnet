using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// The App Percy entry point: takes one screenshot of the device under test and posts it to the
    /// CLI. Session-scoped, so the healthcheck and capability reads happen once for a whole Tosca
    /// test case rather than per step.
    /// </summary>
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

        /// <summary>
        /// Captures <paramref name="name"/>. Returns the CLI's `data` object on success, or null
        /// when Percy is not running, is disabled for the session, or the capture failed and errors
        /// are being ignored. Throws only when the session asked for errors not to be ignored.
        /// </summary>
        public JsonElement? Screenshot(string name, ScreenshotOptions options)
        {
            if (!_isPercyEnabled || !_percyOptions.PercyEnabled()) return null;

            bool ignoreErrors = _percyOptions.IgnoreErrors();
            try
            {
                GenericProvider provider = ProviderResolver.ResolveProvider(_driver, _client, _sessionCache);
                // The provider hands back the CLI's whole response so that App Automate can read the
                // sibling `link` off it; `data` is what a caller wants.
                return Json.Property(provider.Screenshot(name, options), "data");
            }
            catch (Exception e)
            {
                _client.PostFailedEvent(Utils.RedactCredentials(e.Message));

                if (e is PercyException)
                {
                    Utils.Log("The Tosca mobile session could not serve this request. " +
                        "See the message below and the Percy module parameters you can set to work around it.", "warn");
                }
                // Name the exception on the default log line: this method otherwise swallows and
                // returns null, and PostFailedEvent redacts what it forwards — so if this line
                // drops the detail there is no full copy of it anywhere.
                Utils.Log($"Error taking screenshot {name} - {e.GetType().Name}: {e.Message}");
                Utils.Log(e.ToString(), "debug");

                if (!ignoreErrors)
                {
                    throw new PercyException($"Error taking screenshot {name}", e);
                }
                return null;
            }
        }

        /// <summary>Drops this session's cached capability and metadata reads.</summary>
        public void ClearSessionCache() => _sessionCache.Clear();
    }
}
