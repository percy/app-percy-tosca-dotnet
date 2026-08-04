namespace AppPercyTosca.Core
{
    /// <summary>
    /// Everything a single PercyScreenshot step can be told to do. Built from Tosca module
    /// parameters by <see cref="ToscaOptions.Build"/>; the defaults here are what a step with
    /// only a SnapshotName gets.
    /// </summary>
    public class ScreenshotOptions
    {
        /// <summary>
        /// Device label used for the Percy tag and for the static device-dimension lookup.
        /// Left null, it is resolved from the session (or from App Automate's executor).
        /// </summary>
        public string? DeviceName { get; set; }

        /// <summary>Explicit OS name ("Android"/"iOS"). Resolved from the session when null.</summary>
        public string? OsName { get; set; }

        /// <summary>Explicit OS version. Resolved from the session when null.</summary>
        public string? PlatformVersion { get; set; }

        /// <summary>-1 means "resolve from the device" rather than "zero height".</summary>
        public int StatusBarHeight { get; set; } = -1;

        /// <summary>-1 means "resolve from the device" rather than "zero height".</summary>
        public int NavBarHeight { get; set; } = -1;

        /// <summary>0 means "resolve from the device" rather than "zero width".</summary>
        public int ScreenWidth { get; set; }

        /// <summary>0 means "resolve from the device" rather than "zero height".</summary>
        public int ScreenHeight { get; set; }

        public int TopScrollviewOffset { get; set; }
        public int BottomScrollviewOffset { get; set; }

        /// <summary>"portrait", "landscape" or "auto"; null resolves from the session.</summary>
        public string? Orientation { get; set; }

        public bool FullScreen { get; set; }
        public bool FullPage { get; set; }
        public bool IosOptimizedFullpage { get; set; }
        public int? ScreenLengths { get; set; }
        public bool? Sync { get; set; }
        public string? TestCase { get; set; }
        public string? Labels { get; set; }
        public string? ThTestCaseExecutionId { get; set; }

        public List<string> IgnoreRegionXpaths { get; set; } = new List<string>();
        public List<string> IgnoreRegionAccessibilityIds { get; set; } = new List<string>();
        public List<Region> CustomIgnoreRegions { get; set; } = new List<Region>();
        public List<string> ConsiderRegionXpaths { get; set; } = new List<string>();
        public List<string> ConsiderRegionAccessibilityIds { get; set; } = new List<string>();
        public List<Region> CustomConsiderRegions { get; set; } = new List<Region>();

        public string? ScrollableXpath { get; set; }
        public string? ScrollableId { get; set; }
    }
}
