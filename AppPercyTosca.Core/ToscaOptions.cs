using System.Globalization;

namespace AppPercyTosca.Core
{
    /// Turns Tosca module parameters into typed options. Every one arrives as a string or not at all,
    /// so the coercion lives here — in the Core, so it can be tested without Tosca.
    ///
    /// Lenient on purpose: a malformed value logs and falls back to the default, since a typo in one
    /// optional parameter should not stop the snapshot the rest of the row describes.
    public static class ToscaOptions
    {
        /// The module's parameter manifest, in Readme order, including the ones the shim reads itself.
        /// A test asserts Build never reads a name missing from it.
        public static readonly string[] KnownParameters =
        {
            "SnapshotName", "FullScreen", "FullPage", "ScreenLengths",
            "IosOptimizedFullpage", "TopScrollviewOffset", "BottomScrollviewOffset",
            "CustomIgnoreRegions", "CustomConsiderRegions",
            "Labels", "SessionId",
            "LogLevel", "LogFile", "CliApi", "TmpDir",
            "ForceFullPage", "DisableRemoteUploads", "EnablePercyDev", "AutomateDomain"
        };

        /// Module parameters that stand in for environment variables, and the variable each one sets.
        ///
        /// Tosca cannot set environment variables for the process it runs in, so without these rows the
        /// whole set is unreachable from a test sheet. <c>PERCY_TOKEN</c> is deliberately absent: the
        /// CLI reads it, not this SDK, so a value here would be silently ignored.
        public static readonly (string Parameter, string Variable)[] EnvironmentParameters =
        {
            ("LogLevel", "PERCY_LOGLEVEL"),
            ("LogFile", "PERCY_LOG_FILE"),
            ("CliApi", "PERCY_CLI_API"),
            ("TmpDir", "PERCY_TMP_DIR"),
            ("ForceFullPage", "FORCE_FULL_PAGE"),
            ("DisableRemoteUploads", "PERCY_DISABLE_REMOTE_UPLOADS"),
            ("EnablePercyDev", "PERCY_ENABLE_DEV"),
            ("AutomateDomain", "AA_DOMAIN")
        };

        /// Reads a parameter by name, or null when the row is absent or blank.
        public delegate string? ParameterReader(string name);

        /// Applies the environment-variable parameters for one step. Must run before the step's first
        /// log line, since <c>LogLevel</c> and <c>LogFile</c> decide whether and where it is recorded.
        ///
        /// An absent row clears rather than keeps: a parameter deleted from the sheet should stop
        /// applying, and leaving the previous step's value in place on a shared static would make one
        /// step's LogLevel outlive the row that asked for it.
        public static void ApplyEnvironment(ParameterReader read)
        {
            foreach ((string parameter, string variable) in EnvironmentParameters)
            {
                Env.Supply(variable, read(parameter));
            }
        }

        /// Assembles the options for one step.
        public static ScreenshotOptions Build(ParameterReader read)
        {
            ScreenshotOptions options = new ScreenshotOptions
            {
                Labels = Trimmed(read("Labels")),

                ScreenLengths = ParseInt(read("ScreenLengths"), "ScreenLengths"),
                TopScrollviewOffset = ParseInt(read("TopScrollviewOffset"), "TopScrollviewOffset") ?? 0,
                BottomScrollviewOffset =
                    ParseInt(read("BottomScrollviewOffset"), "BottomScrollviewOffset") ?? 0,

                FullScreen = ParseBool(read("FullScreen"), "FullScreen") ?? false,
                FullPage = ParseBool(read("FullPage"), "FullPage") ?? false,
                IosOptimizedFullpage = ParseBool(read("IosOptimizedFullpage"), "IosOptimizedFullpage") ?? false,

                CustomIgnoreRegions = ParseRegions(read("CustomIgnoreRegions"), "CustomIgnoreRegions"),
                CustomConsiderRegions = ParseRegions(read("CustomConsiderRegions"), "CustomConsiderRegions")
            };

            return options;
        }

        /// An unparseable value is reported: treating "1O80" as unset explains nothing.
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

        /// Accepts True/False, 1/0 and Yes/No, case-insensitively.
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

        /// Newlines separate when present, otherwise semicolons. Never commas: an XPath predicate such
        /// as <c>//*[contains(@id,'x')]</c> contains one, and splitting there breaks the locator.
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

        /// Regions written <c>top,bottom,left,right</c>, one per entry: <c>"0,100,0,200; 300,400,0,200"</c>.
        /// Commas separate a region's four numbers; semicolons or newlines separate regions.
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
