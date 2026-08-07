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

        public List<string> IgnoreRegionXpaths { get; set; } = new List<string>();
        public List<string> IgnoreRegionAccessibilityIds { get; set; } = new List<string>();
        public List<Region> CustomIgnoreRegions { get; set; } = new List<Region>();
        public List<string> ConsiderRegionXpaths { get; set; } = new List<string>();
        public List<string> ConsiderRegionAccessibilityIds { get; set; } = new List<string>();
        public List<Region> CustomConsiderRegions { get; set; } = new List<Region>();
    }
}
