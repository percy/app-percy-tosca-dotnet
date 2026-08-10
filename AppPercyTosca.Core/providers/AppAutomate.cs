using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// The one capture path: BrowserStack App Automate. The hub's <c>browserstack_executor</c> takes
    /// the screenshots and uploads them itself, so tiles arrive as content hashes, no image data
    /// passes through Tosca, and full-page capture is possible.
    ///
    /// One local path remains, under <c>PERCY_DISABLE_REMOTE_UPLOADS</c>, as in percy-appium-dotnet:
    /// with uploads off there is no other way to get the image out.
    public class AppAutomate
    {
        protected readonly IMobileDriver Driver;
        protected readonly PercyClient Client;
        protected readonly Cache<string, object?> SessionCache;

        /// The App Automate session URL, shown next to the Percy comparison.
        private string? _debugUrl;

        /// Resolved per snapshot, before anything reads it.
        protected Metadata Metadata { get; private set; } = null!;

        /// Cleared on the first refusal, so begin/end stop re-issuing what the hub will not serve.
        private bool _markedPercySession = true;

        public AppAutomate(IMobileDriver driver, PercyClient client, Cache<string, object?> sessionCache)
        {
            Driver = driver;
            Client = client;
            SessionCache = sessionCache;
        }

        /// Whether this session looks like App Automate. Only reported, not acted on: a session that
        /// is not gets a clear warning instead of an obscure failure on its first executor command.
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

        /// Resolves the device facts, gathers regions, captures, and posts the comparison.
        private JsonElement? Capture(string name, ScreenshotOptions options)
        {
            Metadata = MetadataResolver.Resolve(Driver, SessionCache);

            Dictionary<string, object?> tag = Metadata.GetTag();
            List<Dictionary<string, object?>> ignored = FindRegions(options.CustomIgnoreRegions);
            List<Dictionary<string, object?>> considered = FindRegions(options.CustomConsiderRegions);

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

        /// Asks the hub to capture and returns the tile descriptors it uploaded, or writes one tile
        /// locally when remote uploads are off.
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
                // Include the payload: a bare "error" cannot distinguish a refusal from a bad response.
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

        /// Session URL on the App Automate dashboard, shown next to the Percy comparison.
        public string? GetDebugUrl(JsonElement? result)
        {
            string? buildHash = Json.PropertyAsString(result, "buildHash");
            string? sessionHash = Json.PropertyAsString(result, "sessionHash");
            if (buildHash == null || sessionHash == null) return null;
            return $"https://app-automate.browserstack.com/dashboard/v2/builds/{buildHash}/sessions/{sessionHash}";
        }

        /// Links the App Automate session to the Percy build. Best-effort: a hub that will not take
        /// the marker should not stop the capture.
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

        /// Writes the pass/fail status into the App Automate session log, which for a failed
        /// screenshot is often the last surviving record — so a failure here is logged, not swallowed.
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

        /// Issues the actual capture command and returns the raw tile array as a JSON string.
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
                    ["topScrollviewOffset"] = options.TopScrollviewOffset,
                    ["bottomScrollviewOffset"] = options.BottomScrollviewOffset,
                    // No scrollable-element options: the hub picks the view itself, and nothing here
                    // could validate an XPath typed into a test sheet.
                    ["iosOptimizedFullpage"] = options.IosOptimizedFullpage,
                    ["FORCE_FULL_PAGE"] = Env.ForceFullPage()
                }
            },
            // Sized to the capture, not to the SDK's other traffic: this one request does not return
            // until the hub has captured and stitched every tile.
            CaptureBudget.For(options));

            string? payloadText = result?.GetRawText();

            // A refusal is {"success": false, "message": ...} with no "result" key, and that message
            // is the only account of why the snapshot did not happen.
            JsonElement? payload = Json.Property(result, "result");
            if (payload == null)
            {
                string? message = Json.PropertyAsString(result, "message");
                bool refused = Json.Property(result, "success")?.ValueKind == JsonValueKind.False;
                // The raw response too: "no message" alone reads as a hub problem when it is usually
                // the session id or the command being rejected outright.
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

        private JsonElement? ExecuteBrowserstackCommand(
            Dictionary<string, object?> arguments, TimeSpan? timeout = null)
        {
            string command = "browserstack_executor: " + PercyPayload.PayloadParser(
                new Dictionary<string, object?>
                {
                    ["action"] = "percyScreenshot",
                    ["arguments"] = arguments
                });

            // Both directions: this exchange decides whether a snapshot happens at all, and without
            // it a refusal surfaces downstream with nothing to say what was asked or what came back.
            Utils.Log($"browserstack_executor -> {Truncate(command)}", "debug");

            string? response = Driver.ExecuteScript(command, timeout);

            Utils.Log($"browserstack_executor <- {Truncate(response)}", "debug");
            return Json.TryParse(response);
        }

        /// Caps a logged payload: a fullpage response is a tile array, not a log line.
        private static string Truncate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(nothing)";
            string redacted = Utils.RedactCredentials(text.Trim());
            return redacted.Length <= 500 ? redacted : redacted.Substring(0, 500) + "…";
        }

        /// Only reached under PERCY_DISABLE_REMOTE_UPLOADS; otherwise the hub uploads for us.
        private List<Tile> LocalTile(ScreenshotOptions options)
        {
            string localFilePath = WriteTile(Driver.GetScreenshotBase64());
            return new List<Tile>
            {
                new Tile(localFilePath, Metadata.StatBarHeight(), Metadata.NavBarHeight(),
                    0, 0, options.FullScreen)
            };
        }

        /// Writes the PNG the CLI reads by path, so the file deliberately outlives this call and is
        /// left to the OS temp sweep — the same contract the other App Percy SDKs use.
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

        /// Turns every declared region into the payload shape, validated against the screen.
        ///
        /// Pixel coordinates only. Locator-based regions are not accepted as parameters, because the
        /// Tosca mobile engine cannot be queried for elements from an extension — a locator row could
        /// never have resolved to anything.
        public List<Dictionary<string, object?>> FindRegions(List<Region> customRegions)
        {
            List<Dictionary<string, object?>> regions = new List<Dictionary<string, object?>>();
            if (customRegions.Count == 0) return regions;

            int width = Metadata.DeviceScreenWidth();
            int height = Metadata.DeviceScreenHeight();

            // With no screen size, IsValid would reject every region for exceeding a zero-sized
            // screen — blaming the region rather than the missing dimensions, which GetTag has already
            // warned about. Passing them through unchecked respects what was actually asked for.
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

            return regions;
        }
    }
}
