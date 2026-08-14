namespace AppPercyTosca.Core
{
    /// Android device facts. An Appium Android session reports its full screen size in
    /// `deviceScreenSize` and its usable area in `viewportRect`; the bars are the difference.
    public class AndroidMetadata : Metadata
    {
        private readonly Cache<string, object?> _cache;
        private readonly string _sessionId;

        public AndroidMetadata(IMobileDriver driver, Cache<string, object?> cache)
            : base(driver)
        {
            _cache = cache;
            _sessionId = driver.SessionId;
        }

        public override string OsName() => "Android";

        protected override int MeasuredScreenWidth()
        {
            return ScreenSize()?.Width ?? ViewportInt("width") ?? 0;
        }

        protected override int MeasuredScreenHeight()
        {
            (int Width, int Height)? screen = ScreenSize();
            if (screen != null) return screen.Value.Height;

            // No deviceScreenSize: rebuild the full height from the usable area plus the bars we
            // know about, so the tag is still roughly right rather than 0.
            int? viewportHeight = ViewportInt("height");
            if (viewportHeight == null) return 0;
            return viewportHeight.Value + StatBarHeight() + NavBarHeight();
        }

        public override int StatBarHeight()
        {
            return ViewportInt("top") ?? 0;
        }

        public override int NavBarHeight()
        {
            // Needs both the full height and the usable area. Without deviceScreenSize, 0 is the
            // honest answer — deriving it from DeviceScreenHeight() would recurse.
            (int Width, int Height)? screen = ScreenSize();
            int? viewportHeight = ViewportInt("height");
            if (screen == null || viewportHeight == null) return 0;

            int navBar = screen.Value.Height - (viewportHeight.Value + StatBarHeight());
            // A viewport spanning the screen has no nav bar; a negative crop would corrupt the tag.
            return navBar > 0 ? navBar : 0;
        }

        public override int ScaleFactor() => 1;

        /// Parses the `deviceScreenSize` capability ("1080x1920"), or null.
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
