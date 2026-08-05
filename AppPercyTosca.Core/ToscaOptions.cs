using System.Globalization;
using System.Text.Json;

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
        /// Every parameter name an AppPercyScreenshot module can carry, in the order the Readme documents
        /// them — including the ones the shim reads itself (SnapshotName, SessionIdBuffer, Diagnose)
        /// rather than only those the builders below read. It is the module's parameter manifest, and a
        /// test asserts the builders never read a name missing from it.
        /// </summary>
        public static readonly string[] KnownParameters =
        {
            "SnapshotName", "DeviceName", "OsName", "OsVersion", "StatusBarHeight", "NavBarHeight",
            "ScreenWidth", "ScreenHeight", "Orientation", "FullScreen", "FullPage", "ScreenLengths",
            "IosOptimizedFullpage", "TopScrollviewOffset", "BottomScrollviewOffset",
            "ScrollableXpath", "ScrollableId", "IgnoreRegionXpaths", "IgnoreRegionAccessibilityIds",
            "CustomIgnoreRegions", "ConsiderRegionXpaths", "ConsiderRegionAccessibilityIds",
            "CustomConsiderRegions", "Sync", "TestCase", "Labels", "ThTestCaseExecutionId",
            "FreezeAnimatedImage", "FreezeImageByXpaths", "PercyCSS",
            "IgnoreRegionSelectors", "ConsiderRegionSelectors",
            "SessionIdBuffer", "Diagnose", "Options",
            // Read by Tosca's own screenshot task off the same test action, not by the builders below.
            "Directory", "Filename"
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
        /// Assembles the loosely-typed option bag for a Percy on Automate snapshot. The CLI owns
        /// this schema, so the keys use its snake_case spellings and anything the step did not set
        /// is omitted entirely — sending an explicit null would override a project default.
        ///
        /// A raw JSON <c>Options</c> parameter is merged last so a user can reach a CLI option this
        /// SDK has no named parameter for, without waiting for a new release.
        /// </summary>
        public static Dictionary<string, object?> BuildAutomateOptions(ParameterReader read)
        {
            Dictionary<string, object?> options = new Dictionary<string, object?>();

            Put(options, "device_name", Trimmed(read("DeviceName")));
            Put(options, "orientation", Trimmed(read("Orientation")));
            Put(options, "status_bar_height", ParseInt(read("StatusBarHeight"), "StatusBarHeight"));
            Put(options, "nav_bar_height", ParseInt(read("NavBarHeight"), "NavBarHeight"));
            Put(options, "top_scrollview_offset", ParseInt(read("TopScrollviewOffset"), "TopScrollviewOffset"));
            Put(options, "bottom_scrollview_offset", ParseInt(read("BottomScrollviewOffset"), "BottomScrollviewOffset"));
            Put(options, "screen_lengths", ParseInt(read("ScreenLengths"), "ScreenLengths"));
            Put(options, "full_screen", ParseBool(read("FullScreen"), "FullScreen"));
            // "full_page", not "fullpage". The CLI camelCases every option key before reading it, and
            // it reads `fullPage`. A key with no separator is left alone by that conversion, so
            // "fullpage" stays "fullpage" and never matches — full page capture would silently
            // degrade to a single screen, which is the main reason to choose Percy on Automate.
            Put(options, "full_page", ParseBool(read("FullPage"), "FullPage"));
            Put(options, "ios_optimized_fullpage", ParseBool(read("IosOptimizedFullpage"), "IosOptimizedFullpage"));
            Put(options, "sync", ParseBool(read("Sync"), "Sync"));
            Put(options, "test_case", Trimmed(read("TestCase")));
            Put(options, "labels", Trimmed(read("Labels")));
            Put(options, "th_test_case_execution_id", Trimmed(read("ThTestCaseExecutionId")));
            Put(options, "scrollable_xpath", Trimmed(read("ScrollableXpath")));
            Put(options, "scrollable_id", Trimmed(read("ScrollableId")));
            // Freezing animation is worth a named parameter rather than leaving it to the raw Options
            // escape hatch: an animated splash or loading spinner is one of the most common causes of
            // a flaky mobile comparison, and the CLI does all the work.
            Put(options, "freeze_animated_image", ParseBool(read("FreezeAnimatedImage"), "FreezeAnimatedImage"));
            PutList(options, "freeze_image_by_xpaths", ParseLocatorList(read("FreezeImageByXpaths")));
            // Pure pass-through to the CLI, so there is no reason to make a user reach them through
            // the raw Options JSON. Selectors and percyCSS apply to webviews, which a Tosca mobile
            // test can legitimately be driving.
            Put(options, "percy_css", Trimmed(read("PercyCSS")));
            PutList(options, "ignore_region_selectors", ParseLocatorList(read("IgnoreRegionSelectors")));
            PutList(options, "consider_region_selectors", ParseLocatorList(read("ConsiderRegionSelectors")));

            // XPaths go under the xpath keys, which the CLI resolves against the session itself.
            // Not under ignore_region_appium_elements — that key means a list of Appium element
            // objects, which this SDK cannot produce and which would send the locators through
            // local element resolution that a Tosca session cannot perform, dropping every region.
            PutList(options, "ignore_region_xpaths", ParseLocatorList(read("IgnoreRegionXpaths")));
            PutList(options, "consider_region_xpaths", ParseLocatorList(read("ConsiderRegionXpaths")));
            PutList(options, "custom_ignore_regions", RegionPayloads(ParseRegions(read("CustomIgnoreRegions"), "CustomIgnoreRegions")));
            PutList(options, "custom_consider_regions", RegionPayloads(ParseRegions(read("CustomConsiderRegions"), "CustomConsiderRegions")));

            // Accessibility ids are an App Percy feature: the SDK resolves them to coordinates
            // locally, which needs a driver Tosca does not expose. Percy on Automate has no
            // equivalent option, so silently forwarding them would leave the region unapplied with
            // nothing said about it.
            WarnIfSet(read, "IgnoreRegionAccessibilityIds");
            WarnIfSet(read, "ConsiderRegionAccessibilityIds");

            foreach (KeyValuePair<string, object?> extra in ParseRawOptions(read("Options")))
            {
                options[extra.Key] = extra.Value;
            }

            return options;
        }

        /// <summary>
        /// Parses the free-form <c>Options</c> parameter (a JSON object). Anything that is not a
        /// JSON object is reported and ignored rather than thrown, so one bad row does not stop the
        /// snapshot.
        /// </summary>
        public static Dictionary<string, object?> ParseRawOptions(string? raw)
        {
            Dictionary<string, object?> empty = new Dictionary<string, object?>();
            if (string.IsNullOrWhiteSpace(raw)) return empty;

            JsonElement? parsed = Json.TryParse(raw);
            if (parsed == null || parsed.Value.ValueKind != JsonValueKind.Object)
            {
                Utils.Log("The Options parameter is not a JSON object, ignoring it.", "warn");
                return empty;
            }
            return Json.ToObject(parsed.Value) as Dictionary<string, object?> ?? empty;
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

        private static List<Dictionary<string, object?>> RegionPayloads(List<Region> regions) =>
            regions.Select(r => new Dictionary<string, object?>
            {
                ["top"] = r.Top,
                ["bottom"] = r.Bottom,
                ["left"] = r.Left,
                ["right"] = r.Right
            }).ToList();

        private static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// Reports a parameter that has no effect on this path, rather than accepting it silently.
        /// </summary>
        private static void WarnIfSet(ParameterReader read, string parameter)
        {
            if (ParseLocatorList(read(parameter)).Count == 0) return;
            Utils.Log($"{parameter} is not supported on Tosca — element lookups need a driver the " +
                "Tosca mobile engine does not expose, and Percy on Automate has no accessibility-id " +
                "option. Use IgnoreRegionXpaths, or CustomIgnoreRegions with pixel coordinates.",
                "warn");
        }

        private static void Put(Dictionary<string, object?> options, string key, object? value)
        {
            if (value != null) options[key] = value;
        }

        private static void PutList<T>(Dictionary<string, object?> options, string key, List<T> values)
        {
            if (values.Count > 0) options[key] = values;
        }
    }
}
