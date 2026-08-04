namespace AppPercyTosca.Core
{
    /// <summary>
    /// Picks the platform-specific metadata implementation for a session.
    /// </summary>
    public static class MetadataResolver
    {
        /// <summary>
        /// Resolves by platform name, preferring what the step declared over what the session
        /// reports — a Tosca mobile session may not expose `platformName` at all, and OsName on
        /// the module is the documented way to say so.
        /// </summary>
        public static Metadata Resolve(IMobileDriver driver, ScreenshotOptions options, Cache<string, object?> cache)
        {
            string platform = (options.OsName ?? driver.PlatformName ?? "").Trim();

            if (platform.Contains("android", StringComparison.OrdinalIgnoreCase))
            {
                return new AndroidMetadata(driver, options, cache);
            }
            if (platform.Contains("ios", StringComparison.OrdinalIgnoreCase) ||
                platform.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
                platform.Contains("ipad", StringComparison.OrdinalIgnoreCase))
            {
                return new IosMetadata(driver, options, cache);
            }

            // Named rather than silently defaulted: a wrong platform yields a tag with the wrong
            // dimensions, which shows up as a whole-screen visual diff that is hard to trace back
            // to a missing capability.
            throw new PercyException(
                "Could not determine the device platform" +
                (string.IsNullOrWhiteSpace(platform) ? "" : $" from '{platform}'") +
                ". Set the OsName parameter on the Percy module to \"Android\" or \"iOS\".");
        }
    }
}
