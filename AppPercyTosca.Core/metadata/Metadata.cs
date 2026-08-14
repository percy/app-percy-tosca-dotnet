namespace AppPercyTosca.Core
{
    /// The device facts Percy tags a comparison with, from the session and the static device table
    /// only. No step parameter can declare them: the session knows which device was allocated, and a
    /// stale or mistyped override splits a baseline in a way that looks like a real visual change.
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

            // The other SDKs default to portrait and only query on "auto", to save a round trip they
            // would otherwise make every snapshot. This one is already talking to the session, and
            // assuming portrait on a landscape device gets the dimensions wrong too.
            string? live = Driver.Orientation;
            return string.IsNullOrWhiteSpace(live) ? "portrait" : live.ToLowerInvariant();
        }

        public string? PlatformVersion()
        {
            return Driver.Capabilities.GetString("platformVersion")
                ?? Driver.Capabilities.GetString("os_version");
        }

        /// The device this snapshot was taken on, which Percy groups baselines by.
        ///
        /// One rule for both platforms, because the same capability means different things on each:
        /// `device` is the resolved model on Android but the device *family* on iOS — "iphone" for every
        /// iPhone ever allocated — and `deviceName` is the friendly name on iOS but the UDID on Android.
        /// Reading them in a fixed order per platform is what let a value that cannot identify one
        /// device reach the tag, merging several devices into one baseline.
        ///
        /// So: the order Android already used, with anything that cannot tell two devices apart skipped.
        /// `device` stays first deliberately — it is what Android has always tagged with ("google pixel
        /// 6"), and preferring the tidier `deviceModel` ("Pixel 6") would rename every Android tag and
        /// split every existing baseline.
        public virtual string? DeviceName()
        {
            foreach (string key in new[] { "device", "deviceName", "deviceModel" })
            {
                string? value = Driver.Capabilities.GetString(key)?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                if (IsDeviceFamily(value) || IsIdentifier(value)) continue;
                return value;
            }

            // Nothing that names a model. Said out loud because the consequence is invisible otherwise:
            // every device gets one tag and their diffs land on top of each other.
            string? remaining = Driver.Capabilities.GetString("deviceName")
                ?? Driver.Capabilities.GetString("device");
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                Utils.Log($"The session did not report a device model, so this snapshot is tagged " +
                    $"'{remaining}'. If several devices run under that name their baselines merge — the " +
                    "model is read from the session, so check that SessionId reaches the right one.",
                    "warn");
            }
            return remaining;
        }

        /// "iphone" / "ipad" name a family, not a device. BrowserStack reports one as `device` on iOS.
        private static bool IsDeviceFamily(string value) =>
            value.Equals("iphone", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ipad", StringComparison.OrdinalIgnoreCase)
            || value.Equals("android", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ios", StringComparison.OrdinalIgnoreCase);

        /// A UDID rather than a name — "1A131FDF6009SA", "00008130-000E15C21EA2001C". Unusable as a tag
        /// for a second reason beyond being unreadable: App Automate allocates a different physical
        /// device per run, so a UDID would split the baseline every time.
        private static bool IsIdentifier(string value) =>
            !value.Contains(' ') && value.Length >= 12 && value.Any(char.IsDigit);

        public abstract string OsName();
        public abstract int StatBarHeight();
        public abstract int NavBarHeight();
        public abstract int ScaleFactor();

        /// The screen size as the platform reports it, before orientation is considered.
        protected abstract int MeasuredScreenWidth();

        protected abstract int MeasuredScreenHeight();

        /// The screen size in the orientation the device is actually in. A platform reports its
        /// physical screen — 1080x2400 whichever way up the phone is held — but the image Percy diffs
        /// is 2400x1080 in landscape, and a tag that disagrees with the image splits the baseline.
        public int DeviceScreenWidth() =>
            IsLandscape && MeasuredScreenWidth() < MeasuredScreenHeight()
                ? MeasuredScreenHeight()
                : MeasuredScreenWidth();

        public int DeviceScreenHeight() =>
            IsLandscape && MeasuredScreenWidth() < MeasuredScreenHeight()
                ? MeasuredScreenWidth()
                : MeasuredScreenHeight();

        /// Swapped only when the report is portrait-shaped, so a platform that already accounts
        /// for rotation does not get its correct answer reversed.
        private bool IsLandscape =>
            Orientation().Equals("landscape", StringComparison.OrdinalIgnoreCase);

        /// Identifies which device and screen a comparison belongs to. Percy groups by it, so anything
        /// here that varies run to run splits one baseline into several.
        public Dictionary<string, object?> GetTag()
        {
            int width = DeviceScreenWidth();
            int height = DeviceScreenHeight();

            // A zero dimension is a corrupt baseline key, not a cosmetic gap. There is no parameter
            // to set instead, so this points at the two reasons the session might not be answering.
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
