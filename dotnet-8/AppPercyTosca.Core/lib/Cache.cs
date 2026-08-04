namespace AppPercyTosca.Core
{
    /// <summary>
    /// Per-session memo store. Device metadata and capability lookups are re-read for every
    /// snapshot in a test case; caching them keyed on session id keeps a Tosca test sheet with
    /// dozens of PercyScreenshot steps from re-querying the device each time.
    /// </summary>
    public class Cache<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _cache = new Dictionary<TKey, TValue>();

        public void Store(TKey key, TValue value) => _cache[key] = value;

        public TValue? Get(TKey key) => _cache.TryGetValue(key, out TValue? value) ? value : default;

        public bool Has(TKey key) => _cache.ContainsKey(key);

        public void Remove(TKey key) => _cache.Remove(key);

        public void Clear() => _cache.Clear();
    }
}
