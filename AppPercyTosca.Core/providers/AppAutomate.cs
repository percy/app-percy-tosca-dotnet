using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// The one capture path: BrowserStack App Automate.
    ///
    /// The hub's <c>browserstack_executor</c> takes the screenshots and uploads them itself, so tiles
    /// arrive as content hashes and no image data passes through Tosca — which is also what makes
    /// full-page capture possible.
    ///
    /// There used to be a second, generic provider for non-App-Automate devices, chosen by a resolver.
    /// Both are gone: this SDK targets App Automate, and the local-capture path only existed because
    /// scripting was believed impossible from Tosca. It is not — the hub takes scripts over HTTP — so
    /// the fallback bought nothing except a quiet downgrade that lost full page.
    ///
    /// One local path remains, under <c>PERCY_DISABLE_REMOTE_UPLOADS</c>, exactly as in
    /// percy-appium-dotnet: when uploads are switched off there is no other way to get the image out.
    /// </summary>
    public class AppAutomate
    {
        protected readonly IMobileDriver Driver;
        protected readonly PercyClient Client;
        protected readonly Cache<string, object?> SessionCache;

        /// <summary>The App Automate session URL, shown next to the Percy comparison.</summary>
        private string? _debugUrl;

        /// <summary>Resolved per snapshot, before anything reads it.</summary>
        protected Metadata Metadata { get; private set; } = null!;

        /// <summary>
        /// Cleared the first time the executor refuses a percyScreenshot command, which stops the
        /// remaining begin/end calls for the run from re-issuing commands the hub will not serve.
        /// </summary>
        private bool _markedPercySession = true;

        public AppAutomate(IMobileDriver driver, PercyClient client, Cache<string, object?> sessionCache)
        {
            Driver = driver;
            Client = client;
            SessionCache = sessionCache;
        }

        /// <summary>
        /// Whether this session looks like App Automate. No longer used to choose between providers —
        /// there is only one — but reported so a non-App-Automate session is called out rather than
        /// failing on its first executor command with something less obvious.
        /// </summary>
        public static bool Supports(IMobileDriver driver)
        {
            string? host = driver.Host;
            return !string.IsNullOrEmpty(host)
                && host.Contains(Env.AutomateDomain(), StringComparison.OrdinalIgnoreCase);
        }

        public JsonElement? Screenshot(string name, ScreenshotOptions options)
        {
            if (!Supports(Driver))
            {
                Utils.Log("This session does not look like BrowserStack App Automate " +
                    $"(AppiumServer is {Utils.RedactCredentials(Driver.Host) ?? "unset"}). App Percy for " +
                    "Tosca captures through App Automate, so the commands below may be refused.", "warn");
            }

            JsonElement? result = ExecutePercyScreenshotBegin(name);

            SetDebugUrl(GetDebugUrl(result));

            string? percyScreenshotUrl = null;
            string? error = null;
            try
            {
                JsonElement? data = Capture(name, options);
                percyScreenshotUrl = Json.PropertyAsString(data, "link");
                return data;
            }
            catch (Exception e)
            {
                // statusMessage is persisted into the hub's session log, so redact there too.
                error = Utils.RedactCredentials(e.Message);
                // `throw;` not `throw e;` — the latter resets the stack trace to this line.
                throw;
            }
            finally
            {
                ExecutePercyScreenshotEnd(name, percyScreenshotUrl, error);
            }
        }

        /// <summary>
        /// Resolves the device facts, gathers regions, captures, and posts the comparison. Previously
        /// inherited; inlined now that this is the only provider.
        /// </summary>
        private JsonElement? Capture(string name, ScreenshotOptions options)
        {
            Metadata = MetadataResolver.Resolve(Driver, SessionCache);

            Dictionary<string, object?> tag = Metadata.GetTag();
            List<Dictionary<string, object?>> ignored = FindRegions(
                options.IgnoreRegionXpaths,
                options.IgnoreRegionAccessibilityIds,
                options.CustomIgnoreRegions);
            List<Dictionary<string, object?>> considered = FindRegions(
                options.ConsiderRegionXpaths,
                options.ConsiderRegionAccessibilityIds,
                options.CustomConsiderRegions);

            List<Tile> tiles = CaptureTiles(options);

            return Client.PostScreenshot(
                name,
                tag,
                tiles,
                _debugUrl,
                new Dictionary<string, object?> { ["ignoreElementsData"] = ignored },
                new Dictionary<string, object?> { ["considerElementsData"] = considered },
                options);
        }

        private void SetDebugUrl(string? debugUrl) => _debugUrl = debugUrl;

        /// <summary>
        /// Asks the hub to capture the screen(s) and returns the tile descriptors it uploaded.
        /// Falls back to a locally-written tile when remote uploads are switched off.
        /// </summary>
        public List<Tile> CaptureTiles(ScreenshotOptions options)
        {
            if (Env.DisableRemoteUploads())
            {
                if (options.FullPage)
                {
                    Utils.Log("Full page screenshots are only supported when " +
                        "\"isDisableRemoteUpload\" is not set", "warn");
                }
                return LocalTile(options);
            }

            int statusBar = Metadata.StatBarHeight();
            int navBar = Metadata.NavBarHeight();
            string payload = ExecutePercyScreenshot(options);

            JsonElement? parsed = Json.TryParse(payload);
            if (parsed == null || parsed.Value.ValueKind != JsonValueKind.Array)
            {
                // Say what could not be parsed: a bare "error" here left no way to tell a hub
                // refusal apart from a malformed response.
                throw new PercyException(
                    "Could not parse the tile data returned by the percyScreenshot executor: " + payload);
            }

            List<Tile> tiles = new List<Tile>();
            foreach (JsonElement tile in parsed.Value.EnumerateArray())
            {
                // The sha arrives suffixed ("<sha>-<n>"); only the hash itself is the tile key.
                string? sha = Json.PropertyAsString(tile, "sha")?.Split('-')[0];
                int headerHeight = Capabilities.ToInt(Json.PropertyAsString(tile, "header_height")) ?? 0;
                int footerHeight = Capabilities.ToInt(Json.PropertyAsString(tile, "footer_height")) ?? 0;
                tiles.Add(new Tile(null, statusBar, navBar, headerHeight, footerHeight,
                    options.FullScreen, sha));
            }
            return tiles;
        }

        /// <summary>Session URL on the App Automate dashboard, shown next to the Percy comparison.</summary>
        public string? GetDebugUrl(JsonElement? result)
        {
            string? buildHash = Json.PropertyAsString(result, "buildHash");
            string? sessionHash = Json.PropertyAsString(result, "sessionHash");
            if (buildHash == null || sessionHash == null) return null;
            return $"https://app-automate.browserstack.com/dashboard/v2/builds/{buildHash}/sessions/{sessionHash}";
        }


        /// <summary>
        /// Marks the start of a Percy screenshot on the hub, which is what links the App Automate
        /// session to the Percy build. Best-effort: a hub that will not take the marker should not
        /// stop us capturing.
        /// </summary>
        public JsonElement? ExecutePercyScreenshotBegin(string name)
        {
            if (!_markedPercySession) return null;
            try
            {
                JsonElement? result = ExecuteBrowserstackCommand(new Dictionary<string, object?>
                {
                    ["state"] = "begin",
                    ["percyBuildId"] = Env.PercyBuildId,
                    ["percyBuildUrl"] = Env.PercyBuildUrl,
                    ["name"] = name
                });
                _markedPercySession = Json.IsTrue(result, "success");
                return result;
            }
            catch (Exception e)
            {
                Utils.Log("BrowserStack executor failed at percyScreenshot begin");
                Utils.Log(e.ToString(), "debug");
                return null;
            }
        }

        /// <summary>
        /// Reports the outcome back to the hub. This is what writes the pass/fail status into the
        /// App Automate session log, and for a failed screenshot it is often the last surviving
        /// record — so a failure inside this call is logged rather than swallowed.
        /// </summary>
        public JsonElement? ExecutePercyScreenshotEnd(
            string name, string? percyScreenshotUrl, string? error)
        {
            if (!_markedPercySession) return null;
            try
            {
                JsonElement? result = ExecuteBrowserstackCommand(new Dictionary<string, object?>
                {
                    ["state"] = "end",
                    ["percyScreenshotUrl"] = percyScreenshotUrl,
                    ["status"] = error == null ? "success" : "failure",
                    ["statusMessage"] = error,
                    ["name"] = name
                });
                _markedPercySession = Json.IsTrue(result, "success");
                return result;
            }
            catch (Exception e)
            {
                Utils.Log("BrowserStack executor failed at percyScreenshot end");
                Utils.Log(e.ToString(), "debug");
                return null;
            }
        }

        /// <summary>
        /// Issues the actual capture command and returns the raw tile array as a JSON string.
        /// </summary>
        public string ExecutePercyScreenshot(ScreenshotOptions options)
        {
            string screenshotType = "fullpage";
            if (!options.FullPage || (options.ScreenLengths != null && options.ScreenLengths < 2))
            {
                screenshotType = "singlepage";
            }

            JsonElement? result = ExecuteBrowserstackCommand(new Dictionary<string, object?>
            {
                ["state"] = "screenshot",
                ["percyBuildId"] = Env.PercyBuildId,
                ["screenshotType"] = screenshotType,
                ["scaleFactor"] = Metadata.ScaleFactor(),
                ["projectId"] = Env.EnablePercyDev() ? "percy-dev" : "percy-prod",
                ["options"] = new Dictionary<string, object?>
                {
                    ["deviceHeight"] = Metadata.DeviceScreenHeight(),
                    ["numOfTiles"] = options.ScreenLengths,
                    // No scrollable-element or offset options are sent: the hub picks the scrollable
                    // view itself, and its default is better than a value typed into a test sheet that
                    // nothing here can validate.
                    ["iosOptimizedFullpage"] = options.IosOptimizedFullpage,
                    ["FORCE_FULL_PAGE"] = Env.ForceFullPage()
                }
            });

            string? payloadText = result?.GetRawText();

            // A refusal comes back as {"success": false, "message": ...} with no "result" key.
            // Reading it blindly would turn the hub's explanation into a null reference and throw
            // away the only account of why the snapshot did not happen.
            JsonElement? payload = Json.Property(result, "result");
            if (payload == null)
            {
                string? message = Json.PropertyAsString(result, "message");
                bool refused = Json.Property(result, "success")?.ValueKind == JsonValueKind.False;
                // The raw response is included because "no message" on its own has sent debugging in
                // the wrong direction: it reads as a hub problem when it is usually the session id or
                // the command being rejected outright.
                throw new PercyException((refused
                    ? $"percyScreenshot {screenshotType} was refused by BrowserStack"
                    : $"percyScreenshot {screenshotType} returned no result") +
                    $": {message ?? "no message"}. The hub replied: {Truncate(payloadText)}");
            }

            // The hub double-encodes `result`: a JSON string containing the tile array.
            return payload.Value.ValueKind == JsonValueKind.String
                ? payload.Value.GetString() ?? ""
                : payload.Value.GetRawText();
        }

        private JsonElement? ExecuteBrowserstackCommand(Dictionary<string, object?> arguments)
        {
            string command = "browserstack_executor: " + PercyPayload.PayloadParser(
                new Dictionary<string, object?>
                {
                    ["action"] = "percyScreenshot",
                    ["arguments"] = arguments
                });

            // Logged both ways. This exchange decides whether a snapshot happens at all, and until now
            // it was the only part of the flow that left no trace — a refusal surfaced as a downstream
            // "returned no result" with nothing to say what was asked or what came back.
            Utils.Log($"browserstack_executor -> {Truncate(command)}", "debug");

            string? response = Driver.ExecuteScript(command);

            Utils.Log($"browserstack_executor <- {Truncate(response)}", "debug");
            return Json.TryParse(response);
        }

        /// <summary>
        /// Caps a logged payload. A fullpage response carries a tile array and the request carries the
        /// whole option set, and neither should turn a log line into a page.
        /// </summary>
        private static string Truncate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(nothing)";
            string redacted = Utils.RedactCredentials(text.Trim());
            return redacted.Length <= 500 ? redacted : redacted.Substring(0, 500) + "…";
        }

        /// <summary>
        /// A single tile captured through the session and written to disk. Only reached when
        /// PERCY_DISABLE_REMOTE_UPLOADS is set, since otherwise the hub uploads for us.
        /// </summary>
        private List<Tile> LocalTile(ScreenshotOptions options)
        {
            string localFilePath = WriteTile(Driver.GetScreenshotBase64());
            return new List<Tile>
            {
                new Tile(localFilePath, Metadata.StatBarHeight(), Metadata.NavBarHeight(),
                    0, 0, options.FullScreen)
            };
        }

        /// <summary>
        /// Decodes a base64 screenshot to a PNG the CLI can read. The CLI reads it from disk by
        /// path, so the file deliberately outlives this call and is cleaned up by the OS temp
        /// sweep — the same contract the other App Percy SDKs use.
        /// </summary>
        private static string WriteTile(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new PercyException(
                    "The session returned an empty screenshot. Check that the device is " +
                    "connected and the app is in the foreground.");
            }

            string dir = Env.TempDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"percy-{Guid.NewGuid()}.png");
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
            return path;
        }

        /// <summary>
        /// Resolves every declared region to device-pixel coordinates. A locator that does not
        /// match is skipped with a log line rather than failing the snapshot: an ignore region for
        /// an element that is legitimately absent on this screen is a normal thing for a Tosca
        /// sheet to declare once and reuse.
        /// </summary>
        public List<Dictionary<string, object?>> FindRegions(
            List<string> xpaths, List<string> accessibilityIds, List<Region> customRegions)
        {
            List<Dictionary<string, object?>> regions = new List<Dictionary<string, object?>>();
            AddRegionsByLocator(regions, xpaths, "xpath", Driver.FindElementByXPath);
            AddRegionsByLocator(regions, accessibilityIds, "id", Driver.FindElementByAccessibilityId);
            AddCustomRegions(regions, customRegions);
            return regions;
        }

        private void AddRegionsByLocator(
            List<Dictionary<string, object?>> regions,
            List<string> locators,
            string kind,
            Func<string, ElementRect?> resolve)
        {
            foreach (string locator in locators)
            {
                try
                {
                    ElementRect? element = resolve(locator);
                    if (element == null)
                    {
                        Utils.Log($"Element with {kind}: {locator} not found. Ignoring this {kind}.");
                        continue;
                    }
                    regions.Add(RegionPayload($"{kind}: {locator}", element));
                }
                catch (Exception e)
                {
                    Utils.Log($"Element with {kind}: {locator} not found. Ignoring this {kind}.");
                    Utils.Log(e.ToString(), "debug");
                }
            }
        }

        private void AddCustomRegions(List<Dictionary<string, object?>> regions, List<Region> customRegions)
        {
            if (customRegions.Count == 0) return;

            int width = Metadata.DeviceScreenWidth();
            int height = Metadata.DeviceScreenHeight();

            // With no known screen size there is nothing to validate against, and IsValid would
            // reject every region for exceeding a zero-sized screen. Passing them through unchecked
            // respects what the user actually asked for; discarding them would silently drop the only
            // region type available on a Tosca session, blaming the region rather than the missing
            // dimensions. GetTag has already warned about those.
            bool canValidate = width > 0 && height > 0;
            if (!canValidate)
            {
                Utils.Log($"Passing {customRegions.Count} custom region(s) through unchecked: the " +
                    "device screen size is unknown, so they cannot be validated against it.", "debug");
            }

            for (int index = 0; index < customRegions.Count; index++)
            {
                Region region = customRegions[index];
                if (canValidate && !region.IsValid(height, width))
                {
                    Utils.Log($"Values passed in custom region at index:- {index} is not valid");
                    continue;
                }
                regions.Add(new Dictionary<string, object?>
                {
                    ["selector"] = $"custom region {index}",
                    ["co_ordinates"] = new Dictionary<string, object?>
                    {
                        ["top"] = region.Top,
                        ["bottom"] = region.Bottom,
                        ["left"] = region.Left,
                        ["right"] = region.Right
                    }
                });
            }
        }

        /// <summary>
        /// Converts an element rect to the region payload. Coordinates are scaled because the
        /// session reports points while the screenshot the CLI diffs is in pixels.
        /// </summary>
        private Dictionary<string, object?> RegionPayload(string selector, ElementRect element)
        {
            int scale = Metadata.ScaleFactor();
            return new Dictionary<string, object?>
            {
                ["selector"] = selector,
                ["co_ordinates"] = new Dictionary<string, object?>
                {
                    ["top"] = element.Y * scale,
                    ["bottom"] = (element.Y + element.Height) * scale,
                    ["left"] = element.X * scale,
                    ["right"] = (element.X + element.Width) * scale
                }
            };
        }
    }
}
