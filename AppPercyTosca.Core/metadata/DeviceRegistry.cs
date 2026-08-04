using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// The static device dimension table shipped with the SDK (pixelRatio, statusBarHeight,
    /// screenWidth, screenHeight), keyed by lower-cased device name. iOS sessions do not report
    /// their screen size, so for those this table is the primary source rather than a fallback.
    /// </summary>
    public static class DeviceRegistry
    {
        internal const string ResourceName = "AppPercyTosca.Core.resources.devices.json";

        private static JsonElement? _devices;
        private static readonly object Lock = new object();

        /// <summary>
        /// Opens the embedded table. Replaceable so tests can exercise the missing-resource and
        /// unreadable-resource fallbacks, which are otherwise only reachable by shipping a broken
        /// assembly — and those fallbacks exist precisely so a broken one degrades to
        /// "device unknown" instead of failing every snapshot.
        /// </summary>
        internal static Func<Stream?> ResourceLoader { get; set; } = () =>
            typeof(DeviceRegistry).Assembly.GetManifestResourceStream(ResourceName);

        /// <summary>
        /// Reads one dimension for one device, or 0 when the device is not in the table — 0 is the
        /// caller's signal to fall back to a session-derived value.
        /// </summary>
        public static int Value(string key, string? deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return 0;

            JsonElement? device = Json.Property(Devices(), deviceName.Trim().ToLowerInvariant());
            if (device == null) return 0;

            JsonElement? value = Json.Property(device, key);
            if (value == null || value.Value.ValueKind != JsonValueKind.Number) return 0;
            return value.Value.TryGetInt32(out int parsed) ? parsed : 0;
        }

        private static JsonElement? Devices()
        {
            lock (Lock)
            {
                if (_devices != null) return _devices;

                try
                {
                    using Stream? stream = ResourceLoader();
                    if (stream == null)
                    {
                        Utils.Log($"Embedded device list {ResourceName} is missing; " +
                            "device dimensions will be read from the session only.", "debug");
                        _devices = default(JsonElement);
                        return _devices;
                    }
                    using StreamReader reader = new StreamReader(stream);
                    _devices = Json.TryParse(reader.ReadToEnd()) ?? default(JsonElement);
                }
                catch (Exception e)
                {
                    // A broken resource must degrade to "device unknown", not fail the snapshot.
                    Utils.Log("Could not read the embedded device list", "debug");
                    Utils.Log(e.ToString(), "debug");
                    _devices = default(JsonElement);
                }
                return _devices;
            }
        }

        /// <summary>Test seam: drops the memoized table and restores the real resource loader.</summary>
        internal static void Reset()
        {
            lock (Lock)
            {
                _devices = null;
                ResourceLoader = () =>
                    typeof(DeviceRegistry).Assembly.GetManifestResourceStream(ResourceName);
            }
        }
    }
}
