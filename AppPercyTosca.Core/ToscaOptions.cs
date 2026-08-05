using System.Globalization;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Turns Tosca module parameters into typed options. Every Tosca parameter arrives as a string
    /// (or as nothing at all when the row is left blank), so all of the coercion and list-splitting
    /// lives here — and lives in the Core rather than the shim so it can be tested without Tosca.
    ///
    /// Parsing is deliberately lenient: a malformed value logs and falls back to the default rather
    /// than failing the step, because a typo in one optional parameter should not stop the snapshot
    /// the rest of the row describes.
    /// </summary>
    public static class ToscaOptions
    {
        /// <summary>
        /// Every parameter name an AppPercyScreenshot module can carry, in the order the Readme
        /// documents them — including the ones the shim reads itself (SnapshotName, SessionIdBuffer,
        /// Diagnose, Directory, Filename) rather than only those Build reads. It is the module's
        /// parameter manifest, and a test asserts Build never reads a name missing from it.
        /// </summary>
        public static readonly string[] KnownParameters =
        {
            "SnapshotName", "DeviceName", "OsName", "OsVersion", "StatusBarHeight", "NavBarHeight",
            "ScreenWidth", "ScreenHeight", "Orientation", "FullScreen", "FullPage", "ScreenLengths",
            "IosOptimizedFullpage", "TopScrollviewOffset", "BottomScrollviewOffset",
            "ScrollableXpath", "ScrollableId", "IgnoreRegionXpaths", "IgnoreRegionAccessibilityIds",
            "CustomIgnoreRegions", "ConsiderRegionXpaths", "ConsiderRegionAccessibilityIds",
            "CustomConsiderRegions", "Sync", "TestCase", "Labels", "ThTestCaseExecutionId",
            "SessionIdBuffer", "Diagnose",
            // Read by Tosca's own screenshot task off the same test action, not by Build below; used
            // only on the PERCY_DISABLE_REMOTE_UPLOADS route.
            "Directory", "Filename", "Environment"
        };

        /// <summary>
        /// Reads a parameter by name, returning null when the row is absent or blank.
        /// </summary>
        public delegate string? ParameterReader(string name);

        /// <summary>
        /// Assembles the options for one AppPercyScreenshot step.
        /// </summary>
        public static ScreenshotOptions Build(ParameterReader read)
        {
            ScreenshotOptions options = new ScreenshotOptions
            {
                DeviceName = Trimmed(read("DeviceName")),
                OsName = Trimmed(read("OsName")),
                PlatformVersion = Trimmed(read("OsVersion")),
                Orientation = Trimmed(read("Orientation")),
                ScrollableXpath = Trimmed(read("ScrollableXpath")),
                ScrollableId = Trimmed(read("ScrollableId")),
                TestCase = Trimmed(read("TestCase")),
                Labels = Trimmed(read("Labels")),
                ThTestCaseExecutionId = Trimmed(read("ThTestCaseExecutionId")),

                // -1, not 0: 0 is a legitimate "there is no status bar" and must be
                // distinguishable from "the step did not say".
                StatusBarHeight = ParseInt(read("StatusBarHeight"), "StatusBarHeight") ?? -1,
                NavBarHeight = ParseInt(read("NavBarHeight"), "NavBarHeight") ?? -1,
                ScreenWidth = ParseInt(read("ScreenWidth"), "ScreenWidth") ?? 0,
                ScreenHeight = ParseInt(read("ScreenHeight"), "ScreenHeight") ?? 0,
                TopScrollviewOffset = ParseInt(read("TopScrollviewOffset"), "TopScrollviewOffset") ?? 0,
                BottomScrollviewOffset = ParseInt(read("BottomScrollviewOffset"), "BottomScrollviewOffset") ?? 0,
                ScreenLengths = ParseInt(read("ScreenLengths"), "ScreenLengths"),

                FullScreen = ParseBool(read("FullScreen"), "FullScreen") ?? false,
                FullPage = ParseBool(read("FullPage"), "FullPage") ?? false,
                IosOptimizedFullpage = ParseBool(read("IosOptimizedFullpage"), "IosOptimizedFullpage") ?? false,
                // Left null when unset so the CLI applies its own default rather than being told
                // "do not sync".
                Sync = ParseBool(read("Sync"), "Sync"),

                IgnoreRegionXpaths = ParseLocatorList(read("IgnoreRegionXpaths")),
                IgnoreRegionAccessibilityIds = ParseLocatorList(read("IgnoreRegionAccessibilityIds")),
                CustomIgnoreRegions = ParseRegions(read("CustomIgnoreRegions"), "CustomIgnoreRegions"),
                ConsiderRegionXpaths = ParseLocatorList(read("ConsiderRegionXpaths")),
                ConsiderRegionAccessibilityIds = ParseLocatorList(read("ConsiderRegionAccessibilityIds")),
                CustomConsiderRegions = ParseRegions(read("CustomConsiderRegions"), "CustomConsiderRegions")
            };

            return options;
        }



        /// <summary>
        /// Parses an integer parameter. A value that is present but unparseable is reported —
        /// silently treating "1O80" as unset would produce a wrong-sized tag with no explanation.
        /// </summary>
        public static int? ParseInt(string? value, string? parameterName = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsed))
            {
                return parsed;
            }
            Utils.Log($"Could not read {parameterName ?? "parameter"} value '{value}' as a number, " +
                "ignoring it.", "warn");
            return null;
        }

        /// <summary>
        /// Parses a boolean parameter. Accepts the spellings a Tosca sheet realistically carries —
        /// True/False, 1/0, Yes/No — case-insensitively.
        /// </summary>
        public static bool? ParseBool(string? value, string? parameterName = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                case "y":
                    return true;
                case "false":
                case "0":
                case "no":
                case "n":
                    return false;
                default:
                    Utils.Log($"Could not read {parameterName ?? "parameter"} value '{value}' as " +
                        "true/false, ignoring it.", "warn");
                    return null;
            }
        }

        /// <summary>
        /// Splits a locator list. Newlines separate when present, otherwise semicolons — commas are
        /// deliberately not a separator, because an XPath predicate such as
        /// <c>//*[contains(@id,'x')]</c> contains one and splitting on it would silently break the
        /// locator into two that match nothing.
        /// </summary>
        public static List<string> ParseLocatorList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();

            char[] separators = value.Contains('\n') || value.Contains('\r')
                ? new[] { '\n', '\r' }
                : new[] { ';' };

            return value.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToList();
        }

        /// <summary>
        /// Parses custom regions written as <c>top,bottom,left,right</c>, one per entry —
        /// e.g. <c>"0,100,0,200; 300,400,0,200"</c>. Here commas separate the four numbers of a
        /// region and semicolons/newlines separate regions.
        /// </summary>
        public static List<Region> ParseRegions(string? value, string? parameterName = null)
        {
            List<Region> regions = new List<Region>();
            foreach (string entry in ParseLocatorList(value))
            {
                string[] parts = entry.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4)
                {
                    Utils.Log($"Could not read {parameterName ?? "region"} entry '{entry}': " +
                        "expected four numbers as top,bottom,left,right. Ignoring it.", "warn");
                    continue;
                }

                int?[] bounds = parts.Select(p => ParseInt(p, parameterName)).ToArray();
                if (bounds.Any(b => b == null))
                {
                    // ParseInt already said which value it could not read.
                    continue;
                }

                try
                {
                    regions.Add(new Region(bounds[0]!.Value, bounds[1]!.Value,
                        bounds[2]!.Value, bounds[3]!.Value));
                }
                catch (ArgumentException e)
                {
                    Utils.Log($"Could not use {parameterName ?? "region"} entry '{entry}': {e.Message}", "warn");
                }
            }
            return regions;
        }


        private static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();



    }
}
