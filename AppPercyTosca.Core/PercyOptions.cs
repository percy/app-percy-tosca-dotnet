namespace AppPercyTosca.Core
{
    /// Reads the `percyOptions` capability bag, which is how a Tosca mobile configuration can turn
    /// Percy off or make it fail loudly without editing the test sheets.
    public class PercyOptions
    {
        private readonly IMobileDriver _driver;
        private readonly Cache<string, object?> _cache;
        private readonly string _sessionId;

        public PercyOptions(IMobileDriver driver, Cache<string, object?> cache)
        {
            _driver = driver;
            _cache = cache;
            _sessionId = driver.SessionId;
        }

        /// Whether Percy is enabled for this session. Nothing declared means enabled — Percy is
        /// opt-out here, so a session with no percyOptions still takes snapshots.
        public bool PercyEnabled()
        {
            IReadOnlyDictionary<string, object?>? w3c = GetPercyOptions();
            object? jsonProtocol = _driver.Capabilities.Raw("percy.enabled");

            if (w3c == null && jsonProtocol == null)
            {
                Utils.Log("Percy options not provided in capabilities, considering enabled", "debug");
                return true;
            }

            object? w3cEnabled = w3c != null && w3c.TryGetValue("enabled", out object? value) ? value : null;
            if (Capabilities.IsFalse(jsonProtocol) || Capabilities.IsFalse(w3cEnabled))
            {
                Utils.Log("App Percy is disabled in capabilities");
                return false;
            }
            return true;
        }

        /// Whether a failed snapshot should fail the Tosca step. Default is to swallow: a visual
        /// check that cannot run is not a functional regression, and failing the step would stop
        /// the rest of the sheet.
        public bool IgnoreErrors()
        {
            IReadOnlyDictionary<string, object?>? w3c = GetPercyOptions();
            object? jsonProtocol = _driver.Capabilities.Raw("percy.ignoreErrors");

            if (w3c == null && jsonProtocol == null)
            {
                Utils.Log("Percy options not provided in capabilities, ignoring errors by default", "debug");
                return true;
            }

            object? w3cIgnore = w3c != null && w3c.TryGetValue("ignoreErrors", out object? value) ? value : null;
            return !(Capabilities.IsFalse(jsonProtocol) || Capabilities.IsFalse(w3cIgnore));
        }

        internal IReadOnlyDictionary<string, object?>? GetPercyOptions()
        {
            string key = "percyOptions_" + _sessionId;
            if (!_cache.Has(key))
            {
                _cache.Store(key, _driver.Capabilities.GetMap("percyOptions"));
            }
            return _cache.Get(key) as IReadOnlyDictionary<string, object?>;
        }
    }
}
