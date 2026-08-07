namespace AppPercyTosca.Core
{
    /// <summary>
    /// Everything a single AppPercyScreenshot step can be told to do. Built from Tosca module
    /// parameters by <see cref="ToscaOptions.Build"/>; the defaults here are what a step with only a
    /// SnapshotName gets.
    ///
    /// Deliberately carries no device details — name, OS, screen size, bar heights, orientation. Those
    /// are read from the session, which knows the device that was actually allocated; a module
    /// parameter could only disagree with it, and a wrong one silently splits a Percy baseline.
    /// </summary>
    public class ScreenshotOptions
    {

        public bool FullScreen { get; set; }
        public bool FullPage { get; set; }
        public bool IosOptimizedFullpage { get; set; }
        public int? ScreenLengths { get; set; }
        public string? Labels { get; set; }

        // Regions are pixel coordinates only. Locator-based regions are not offered: the Tosca mobile
        // engine cannot be queried for elements from an extension, so a locator could never resolve to
        // anything and every such row would have been silently dropped.
        public List<Region> CustomIgnoreRegions { get; set; } = new List<Region>();
        public List<Region> CustomConsiderRegions { get; set; } = new List<Region>();
    }
}
