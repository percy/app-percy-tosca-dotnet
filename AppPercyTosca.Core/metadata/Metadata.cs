namespace AppPercyTosca.Core
{
    /// <summary>
    /// Resolves the device facts Percy tags a comparison with, from the session and the static device
    /// table only.
    ///
    /// There is deliberately no way to declare them on the Tosca step. The session knows which device
    /// was allocated; a module parameter could only ever disagree, and a stale or mistyped one silently
    /// splits a Percy baseline in a way that looks like a real visual change.
    /// </summary>
    public abstract class Metadata
    {
        protected readonly IMobileDriver Driver;

        protected Metadata(IMobileDriver driver)
        {
            Driver = driver;
        }


        public string Orientation()
        {
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
            // baseline key rather than a cosmetic gap. There is no parameter to set instead — the
            // screen size comes from the session or not at all — so this points at the two reasons
            // the session is not answering, which are the only things anyone can act on.
            if (width <= 0 || height <= 0)
            {
                Utils.Log("Could not determine the device screen size, so this snapshot is tagged " +
                    $"{width}x{height} and will not group with correctly-tagged ones. The size is " +
                    "read from the device session: check that the SessionId parameter carries the " +
                    "Appium session id and that AppiumServer points at your hub. Run with " +
                    "PERCY_LOGLEVEL=debug to see what the session did answer.", "warn");
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
