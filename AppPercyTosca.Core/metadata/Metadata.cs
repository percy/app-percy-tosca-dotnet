namespace AppPercyTosca.Core
{
    /// <summary>
    /// Resolves the device facts Percy tags a comparison with. Values explicitly supplied on the
    /// Tosca step always win; anything left unset is read from the session, and only then from the
    /// static device table. That order matters for Tosca specifically: its Mobile engine exposes a
    /// thinner capability set than a raw Appium driver, so being able to declare the dimensions on
    /// the module is the documented escape hatch rather than a fallback nobody uses.
    /// </summary>
    public abstract class Metadata
    {
        protected readonly IMobileDriver Driver;
        private readonly string? _orientation;
        private readonly string? _platformVersion;
        private readonly int _statusBar;
        private readonly int _navBar;
        private readonly string? _deviceName;
        private readonly int _screenWidth;
        private readonly int _screenHeight;

        protected Metadata(IMobileDriver driver, ScreenshotOptions options)
        {
            Driver = driver;
            _deviceName = options.DeviceName;
            _platformVersion = options.PlatformVersion;
            _orientation = options.Orientation;
            _statusBar = options.StatusBarHeight;
            _navBar = options.NavBarHeight;
            _screenWidth = options.ScreenWidth;
            _screenHeight = options.ScreenHeight;
        }

        private int _fallbackWidth;
        private int _fallbackHeight;

        /// <summary>
        /// Supplies the screen size measured from the captured screenshot, used only when nothing
        /// better is available. Called after capture, which is why it is a setter rather than a
        /// constructor argument: on Tosca the screenshot is frequently the only thing that knows how
        /// big the screen is, and it does not exist yet when this object is built.
        /// </summary>
        public void UseFallbackScreenSize(int width, int height)
        {
            if (width > 0) _fallbackWidth = width;
            if (height > 0) _fallbackHeight = height;
        }

        protected int FallbackScreenWidth => _fallbackWidth;
        protected int FallbackScreenHeight => _fallbackHeight;

        protected string? SuppliedDeviceName => _deviceName;
        protected int SuppliedStatusBar => _statusBar;
        protected int SuppliedNavBar => _navBar;
        protected int SuppliedScreenWidth => _screenWidth;
        protected int SuppliedScreenHeight => _screenHeight;

        public string Orientation()
        {
            if (!string.IsNullOrWhiteSpace(_orientation))
            {
                string requested = _orientation.Trim().ToLowerInvariant();
                if (requested == "portrait" || requested == "landscape") return requested;
                // "auto" asks the device; any other spelling is a typo on the module, and
                // assuming portrait matches what the other App Percy SDKs do.
                if (requested == "auto") return Driver.Orientation?.ToLowerInvariant() ?? "portrait";
                return "portrait";
            }

            string? capability = Driver.Capabilities.GetString("orientation");
            return capability?.ToLowerInvariant() ?? "portrait";
        }

        public string? PlatformVersion()
        {
            if (!string.IsNullOrWhiteSpace(_platformVersion)) return _platformVersion;
            return Driver.Capabilities.GetString("platformVersion")
                ?? Driver.Capabilities.GetString("os_version");
        }

        public abstract string? DeviceName();
        public abstract string OsName();
        public abstract int DeviceScreenWidth();
        public abstract int DeviceScreenHeight();
        public abstract int StatBarHeight();
        public abstract int NavBarHeight();
        public abstract int ScaleFactor();

        /// <summary>
        /// The `tag` block identifying which device/screen a comparison belongs to. Percy groups
        /// and compares snapshots by this, so a device name that varies run to run would split one
        /// baseline into several.
        /// </summary>
        public Dictionary<string, object?> GetTag()
        {
            int width = DeviceScreenWidth();
            int height = DeviceScreenHeight();

            // Percy groups and diffs comparisons by this tag, so a zero dimension is a corrupt
            // baseline key rather than a cosmetic gap — and on Tosca it is the likely case, since a
            // mobile session reports no screen size unless a test configuration parameter carries
            // one. Say so once, naming the parameters that fix it.
            if (width <= 0 || height <= 0)
            {
                Utils.Log("Could not determine the device screen size, so this snapshot is tagged " +
                    $"{width}x{height} and will not group with correctly-tagged ones. Set " +
                    "ScreenWidth and ScreenHeight (in pixels) on the Percy module.", "warn");
            }

            return new Dictionary<string, object?>
            {
                ["name"] = DeviceName(),
                ["osName"] = OsName(),
                ["osVersion"] = PlatformVersion(),
                ["width"] = width,
                ["height"] = height,
                ["orientation"] = Orientation()
            };
        }
    }
}
