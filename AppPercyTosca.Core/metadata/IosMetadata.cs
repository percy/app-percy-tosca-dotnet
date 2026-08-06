using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// iOS device facts. An iOS session does not report its physical screen size, so dimensions
    /// come from the static device table first and are only derived from the viewport as a
    /// fallback. The viewport itself is not a capability on iOS either — it takes a
    /// `mobile: viewportRect` script call.
    /// </summary>
    public class IosMetadata : Metadata
    {
        private readonly Cache<string, object?> _cache;
        private readonly string _sessionId;

        public IosMetadata(IMobileDriver driver, ScreenshotOptions options, Cache<string, object?> cache)
            : base(driver, options)
        {
            _cache = cache;
            _sessionId = driver.SessionId;
        }

        public override string OsName() => "iOS";

        public override string? DeviceName()
        {
            if (!string.IsNullOrWhiteSpace(SuppliedDeviceName)) return SuppliedDeviceName;
            return Driver.Capabilities.GetString("deviceName")
                ?? Driver.Capabilities.GetString("device");
        }

        protected override int MeasuredScreenWidth()
        {
            if (SuppliedScreenWidth > 0) return SuppliedScreenWidth;

            int fromTable = DeviceRegistry.Value("screenWidth", DeviceName());
            if (fromTable != 0) return fromTable;
            return ViewportInt("width") ?? 0;
        }

        protected override int MeasuredScreenHeight()
        {
            if (SuppliedScreenHeight > 0) return SuppliedScreenHeight;

            int fromTable = DeviceRegistry.Value("screenHeight", DeviceName());
            if (fromTable != 0) return fromTable;

            int? viewportHeight = ViewportInt("height");
            if (viewportHeight == null) return 0;
            return viewportHeight.Value + StatBarHeight();
        }

        public override int StatBarHeight()
        {
            if (SuppliedStatusBar != -1) return SuppliedStatusBar;

            int statusBar = DeviceRegistry.Value("statusBarHeight", DeviceName());
            if (statusBar == 0) return ViewportInt("top") ?? 0;

            // The table stores the status bar in points; the screenshot is in pixels.
            int pixelRatio = DeviceRegistry.Value("pixelRatio", DeviceName());
            return statusBar * (pixelRatio == 0 ? 1 : pixelRatio);
        }

        /// <summary>iOS has no navigation bar to trim, so this is 0 unless the step declares one.</summary>
        public override int NavBarHeight() => SuppliedNavBar != -1 ? SuppliedNavBar : 0;

        public override int ScaleFactor()
        {
            try
            {
                int? actualWidth = ViewportInt("width");
                int windowWidth = Driver.WindowWidth;
                if (actualWidth == null || actualWidth == 0 || windowWidth <= 0) return 1;
                int factor = actualWidth.Value / windowWidth;
                return factor > 0 ? factor : 1;
            }
            catch (Exception e)
            {
                Utils.Log("Failed to get scale factor, full page screenshot might look incorrect");
                Utils.Log(e.ToString(), "debug");
                return 1;
            }
        }

        private int? ViewportInt(string key)
        {
            IReadOnlyDictionary<string, object?>? rect = ViewportRect();
            if (rect == null) return null;
            return rect.TryGetValue(key, out object? value) ? Capabilities.ToInt(value) : null;
        }

        private IReadOnlyDictionary<string, object?>? ViewportRect()
        {
            string key = "viewportRect_" + _sessionId;
            if (!_cache.Has(key))
            {
                _cache.Store(key, FetchViewportRect());
            }
            return _cache.Get(key) as IReadOnlyDictionary<string, object?>;
        }

        /// <summary>
        /// Asks the session for the viewport. Cached even when it fails (as null), so a session
        /// that cannot serve `mobile: viewportRect` — which Tosca's Mobile engine may not — is
        /// asked once per session rather than once per snapshot.
        /// </summary>
        private IReadOnlyDictionary<string, object?>? FetchViewportRect()
        {
            try
            {
                IReadOnlyDictionary<string, object?>? fromCapability =
                    Driver.Capabilities.GetMap("viewportRect");
                if (fromCapability != null) return fromCapability;

                string? result = Driver.ExecuteScript("mobile: viewportRect");
                if (string.IsNullOrWhiteSpace(result)) return null;

                JsonElement? parsed = Json.TryParse(result);
                if (parsed == null) return null;
                return Capabilities.AsDictionary(parsed.Value);
            }
            catch (Exception e)
            {
                Utils.Log("Could not read the iOS viewport rect from the session", "debug");
                Utils.Log(e.ToString(), "debug");
                return null;
            }
        }
    }
}
