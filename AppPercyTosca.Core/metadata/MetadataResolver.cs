namespace AppPercyTosca.Core
{
    /// <summary>
    /// Picks the platform-specific metadata implementation for a session.
    /// </summary>
    public static class MetadataResolver
    {
        /// <summary>
        /// Resolves by the platform the session reports.
        /// </summary>
        public static Metadata Resolve(IMobileDriver driver, Cache<string, object?> cache)
        {
            string platform = (driver.PlatformName ?? "").Trim();

            if (platform.Contains("android", StringComparison.OrdinalIgnoreCase))
            {
                return new AndroidMetadata(driver, cache);
            }
            if (platform.Contains("ios", StringComparison.OrdinalIgnoreCase) ||
                platform.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
                platform.Contains("ipad", StringComparison.OrdinalIgnoreCase))
            {
                return new IosMetadata(driver, cache);
            }

            // Named rather than silently defaulted: a wrong platform yields a tag with the wrong
            // dimensions, which shows up as a whole-screen visual diff that is hard to trace back
            // to a missing capability.
            throw new PercyException(
                "Could not determine the device platform" +
                (string.IsNullOrWhiteSpace(platform) ? "" : $" from '{platform}'") +
                ". The session should report platformName; check that AppiumServer and SessionId " +
                "reach the device.");
        }
    }
}
