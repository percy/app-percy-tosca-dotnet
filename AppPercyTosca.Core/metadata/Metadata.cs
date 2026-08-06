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
            if (!string.IsNullOrWhiteSpace(capability)) return capability.ToLowerInvariant();

            // Ask the device. The other App Percy SDKs default to portrait here and only query when the
            // caller passes "auto", because asking costs them a driver round trip they would rather not
            // make on every snapshot. This SDK is already talking to the session over HTTP, and
            // defaulting to portrait on a landscape device gets both the orientation and the screen
            // dimensions wrong.
            string? live = Driver.Orientation;
            return string.IsNullOrWhiteSpace(live) ? "portrait" : live.ToLowerInvariant();
        }

        public string? PlatformVersion()
        {
            if (!string.IsNullOrWhiteSpace(_platformVersion)) return _platformVersion;
            return Driver.Capabilities.GetString("platformVersion")
                ?? Driver.Capabilities.GetString("os_version");
        }

        public abstract string? DeviceName();
        public abstract string OsName();
        public abstract int StatBarHeight();
        public abstract int NavBarHeight();
        public abstract int ScaleFactor();

        /// <summary>The screen size as the platform reports it, before orientation is considered.</summary>
        protected abstract int MeasuredScreenWidth();

        protected abstract int MeasuredScreenHeight();

        /// <summary>
        /// The screen size in the orientation the device is actually in.
        ///
        /// A platform reports its physical screen — a phone is 1080x2400 whichever way up it is held —
        /// but the screenshot Percy diffs is 2400x1080 in landscape. Left unswapped, the tag disagreed
        /// with the image, which splits a baseline and makes every custom pixel region fail validation.
        /// </summary>
        public int DeviceScreenWidth() =>
            IsLandscape && MeasuredScreenWidth() < MeasuredScreenHeight()
                ? MeasuredScreenHeight()
                : MeasuredScreenWidth();

        public int DeviceScreenHeight() =>
            IsLandscape && MeasuredScreenWidth() < MeasuredScreenHeight()
                ? MeasuredScreenWidth()
                : MeasuredScreenHeight();

        /// <summary>
        /// Only swapped when the reported size is portrait-shaped. A platform that already accounts for
        /// rotation would otherwise have its correct answer reversed.
        /// </summary>
        private bool IsLandscape =>
            Orientation().Equals("landscape", StringComparison.OrdinalIgnoreCase);

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
