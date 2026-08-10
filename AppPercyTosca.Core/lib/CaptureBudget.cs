namespace AppPercyTosca.Core
{
    /// How long one request to the device session is allowed to take.
    ///
    /// The full-page executor is a single HTTP request that does not return until the hub has captured
    /// every tile, and on iOS each tile costs hundreds of accessibility-tree round trips to a real
    /// device. Measured on an iPhone 14 Pro Max against a 6820pt screen, one capture ran 3m36s at
    /// screenLengths 2 and 5m07s at 5 — so the two-minute client timeout this SDK used abandoned every
    /// iOS full page mid-capture. The error was then swallowed and the build reported no snapshot, which
    /// reads as a broken SDK rather than a client giving up too early.
    ///
    /// Two fixed values rather than a parameter: the user already said what they wanted by asking for a
    /// full page, and a timeout is not a thing anyone can pick a right value for from a test sheet.
    public static class CaptureBudget
    {
        /// A screenshot or a metadata read. Neither scrolls, so neither is slow — but a remote hub on a
        /// bad link still needs more than a moment.
        public static readonly TimeSpan SinglePage = TimeSpan.FromSeconds(150);

        /// A scroll-and-stitch capture. Comfortably above the slowest run measured, and bounded so that
        /// a genuinely stuck capture fails rather than blocking a Tosca step indefinitely.
        public static readonly TimeSpan FullPage = TimeSpan.FromSeconds(500);

        public static TimeSpan For(ScreenshotOptions options) =>
            options.FullPage ? FullPage : SinglePage;
    }
}
