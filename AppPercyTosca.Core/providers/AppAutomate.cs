using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Capture on BrowserStack App Automate. The hub's <c>browserstack_executor</c> takes the
    /// screenshots and uploads them itself, so tiles arrive as content hashes and no image data
    /// passes through Tosca — which also makes full-page capture possible, unlike the local path.
    /// </summary>
    public class AppAutomate : GenericProvider
    {
        /// <summary>
        /// Cleared the first time the executor refuses a percyScreenshot command, which stops the
        /// remaining begin/end calls for the run from re-issuing commands the hub will not serve.
        /// </summary>
        private bool _markedPercySession = true;

        public AppAutomate(IMobileDriver driver, PercyClient client, Cache<string, object?> sessionCache)
            : base(driver, client, sessionCache)
        {
        }

        /// <summary>
        /// True when this provider can actually drive App Automate: the session is running against
        /// an App Automate hub *and* can send raw automation commands.
        ///
        /// The scripting check is not redundant. Every capability this class adds — marking the
        /// session, remote capture, full-page — is issued as a <c>browserstack_executor</c> command,
        /// so on a session that cannot send them (a Tosca mobile session, where Appium passthrough
        /// is restricted to Tricentis' own device cloud) this provider would fail on its first call
        /// while the plain local-capture path would have worked. The host is checked first because
        /// it is the only signal available before any command is sent.
        /// </summary>
        public static bool Supports(IMobileDriver driver)
        {
            string? host = driver.Host;
            if (string.IsNullOrEmpty(host) ||
                !host.Contains(Env.AutomateDomain(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!driver.CanExecuteScript)
            {
                Utils.Log("The session is on App Automate but cannot send automation commands, " +
                    "so screenshots will be captured locally and uploaded. Full page screenshots " +
                    "are not available this way — use Percy on Automate for those.", "debug");
                return false;
            }
            return true;
        }

        public override JsonElement? Screenshot(
            string name, ScreenshotOptions options, string? platformVersion = null)
        {
            JsonElement? result = ExecutePercyScreenshotBegin(name);

            // The executor knows the real device it allocated, which is more trustworthy than the
            // requested capability — but never override what the step explicitly declared.
            if (string.IsNullOrWhiteSpace(options.DeviceName))
            {
                options.DeviceName = Json.PropertyAsString(result, "deviceName");
            }
            SetDebugUrl(GetDebugUrl(result));

            string? percyScreenshotUrl = null;
            string? error = null;
            try
            {
                JsonElement? data = base.Screenshot(name, options, OsVersion(result) ?? platformVersion);
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
                ExecutePercyScreenshotEnd(name, percyScreenshotUrl, options.Sync, error);
            }
        }

        /// <summary>
        /// Asks the hub to capture the screen(s) and returns the tile descriptors it uploaded.
        /// Falls back to the local capture path when remote uploads are switched off.
        /// </summary>
        public override List<Tile> CaptureTiles(ScreenshotOptions options)
        {
            if (Env.DisableRemoteUploads())
            {
                if (options.FullPage)
                {
                    Utils.Log("Full page screenshots are only supported when " +
                        "\"isDisableRemoteUpload\" is not set", "warn");
                }
                return base.CaptureTiles(options);
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

        /// <summary>Major OS version the executor reported, used in preference to the capability.</summary>
        public string? OsVersion(JsonElement? result)
        {
            string? osVersion = Json.PropertyAsString(result, "osVersion");
            if (string.IsNullOrWhiteSpace(osVersion)) return null;
            return osVersion.Split('.')[0];
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
            string name, string? percyScreenshotUrl, bool? sync, string? error)
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
                    ["name"] = name,
                    ["sync"] = sync
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
                    ["scollableXpath"] = options.ScrollableXpath,
                    ["scrollableId"] = options.ScrollableId,
                    ["topScrollviewOffset"] = options.TopScrollviewOffset,
                    ["bottomScrollviewOffset"] = options.BottomScrollviewOffset,
                    ["iosOptimizedFullpage"] = options.IosOptimizedFullpage,
                    ["FORCE_FULL_PAGE"] = Env.ForceFullPage()
                }
            });

            // A refusal comes back as {"success": false, "message": ...} with no "result" key.
            // Reading it blindly would turn the hub's explanation into a null reference and throw
            // away the only account of why the snapshot did not happen.
            JsonElement? payload = Json.Property(result, "result");
            if (payload == null)
            {
                string? message = Json.PropertyAsString(result, "message");
                bool refused = Json.Property(result, "success")?.ValueKind == JsonValueKind.False;
                throw new PercyException(refused
                    ? $"percyScreenshot {screenshotType} was refused by BrowserStack: {message ?? "no message"}"
                    : $"percyScreenshot {screenshotType} returned no result: {message ?? "no message"}");
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

            string? response = Driver.ExecuteScript(command);
            return Json.TryParse(response);
        }
    }
}
