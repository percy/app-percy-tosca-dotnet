namespace AppPercyTosca.Core
{
    /// <summary>
    /// Android device facts. An Appium Android session reports its full screen size in
    /// `deviceScreenSize` and its usable area in `viewportRect`; the bars are the difference.
    /// </summary>
    public class AndroidMetadata : Metadata
    {
        private readonly Cache<string, object?> _cache;
        private readonly string _sessionId;

        public AndroidMetadata(IMobileDriver driver, ScreenshotOptions options, Cache<string, object?> cache)
            : base(driver, options)
        {
            _cache = cache;
            _sessionId = driver.SessionId;
        }

        public override string OsName() => "Android";

        public override string? DeviceName()
        {
            if (!string.IsNullOrWhiteSpace(SuppliedDeviceName)) return SuppliedDeviceName;

            // App Automate reports the resolved device as `device`; a local/desired-caps session
            // only has the requested `deviceName`.
            return Driver.Capabilities.GetString("device")
                ?? Driver.Capabilities.GetString("deviceName");
        }

        public override int DeviceScreenWidth()
        {
            if (SuppliedScreenWidth > 0) return SuppliedScreenWidth;
            return ScreenSize()?.Width ?? ViewportInt("width") ?? FallbackScreenWidth;
        }

        public override int DeviceScreenHeight()
        {
            if (SuppliedScreenHeight > 0) return SuppliedScreenHeight;

            (int Width, int Height)? screen = ScreenSize();
            if (screen != null) return screen.Value.Height;

            // No deviceScreenSize: rebuild the full height from the usable area plus the bars we
            // know about, so the tag is still roughly right rather than 0.
            int? viewportHeight = ViewportInt("height");
            if (viewportHeight == null) return FallbackScreenHeight;
            return viewportHeight.Value + StatBarHeight() + NavBarHeight();
        }

        public override int StatBarHeight()
        {
            if (SuppliedStatusBar != -1) return SuppliedStatusBar;
            return ViewportInt("top") ?? 0;
        }

        public override int NavBarHeight()
        {
            if (SuppliedNavBar != -1) return SuppliedNavBar;

            // Derived, so it needs both the full height and the usable area. Without
            // deviceScreenSize there is nothing to subtract from and 0 is the honest answer —
            // computing it from DeviceScreenHeight() would recurse, since that falls back to
            // adding this value on.
            (int Width, int Height)? screen = ScreenSize();
            int? viewportHeight = ViewportInt("height");
            if (screen == null || viewportHeight == null) return 0;

            int navBar = screen.Value.Height - (viewportHeight.Value + StatBarHeight());
            // A device whose viewport already spans the screen has no nav bar to trim; a negative
            // value would be sent on as a crop and corrupt the comparison.
            return navBar > 0 ? navBar : 0;
        }

        public override int ScaleFactor() => 1;

        /// <summary>
        /// Parses the `deviceScreenSize` capability ("1080x1920"), or null when the session does
        /// not report it. Tosca's Mobile engine is one such session.
        /// </summary>
        private (int Width, int Height)? ScreenSize()
        {
            string? size = Driver.Capabilities.GetString("deviceScreenSize");
            if (string.IsNullOrWhiteSpace(size)) return null;

            string[] parts = size.Split('x');
            if (parts.Length < 2) return null;

            int? width = Capabilities.ToInt(parts[0].Trim());
            int? height = Capabilities.ToInt(parts[1].Trim());
            if (width == null || height == null) return null;
            return (width.Value, height.Value);
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
                _cache.Store(key, Driver.Capabilities.GetMap("viewportRect"));
            }
            return _cache.Get(key) as IReadOnlyDictionary<string, object?>;
        }
    }
}
