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

        public abstract string? DeviceName();
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
