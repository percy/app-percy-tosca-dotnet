using System.Text;
using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Captures the device screen by asking the automation server directly, over plain HTTP.
    ///
    /// This exists because delegating to Tosca's own mobile screenshot task turned out to be a poor
    /// foundation: the task name and engine id differ between Tosca releases, are not documented for
    /// current versions, and a wrong pair fails in a way that looks like a broken device. Meanwhile
    /// the two things actually needed are both available through documented Tosca features — the
    /// Appium session id from the <c>Get Appium Session Id</c> module, and the server address from the
    /// <c>AppiumServer</c> test configuration parameter — and the screenshot endpoint they unlock is
    /// part of the WebDriver standard rather than anything Tricentis-specific.
    ///
    /// So this needs no Tricentis API at all, which also means it is fully testable: the
    /// <see cref="HttpClient"/> is injected.
    /// </summary>
    public class WebDriverSession
    {
        /// <summary>
        /// Attempts made before giving up. The web Tosca SDK retries finding its target ten times for
        /// the same reason: a Tosca step can hand over before the thing it steered is ready. Kept
        /// smaller here because each attempt is a real HTTP round trip to a possibly-remote hub.
        /// </summary>
        public const int Attempts = 3;

        /// <summary>Gap between attempts.</summary>
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        /// <summary>Replaceable so tests do not actually wait.</summary>
        internal static Action<TimeSpan> Sleep { get; set; } = duration => Thread.Sleep(duration);

        private readonly HttpClient _http;
        private readonly string _serverUrl;
        private readonly string _sessionId;

        public WebDriverSession(HttpClient http, string serverUrl, string sessionId)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _serverUrl = (serverUrl ?? throw new ArgumentNullException(nameof(serverUrl))).TrimEnd('/');
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        }

        /// <summary>The screenshot endpoint, exposed for diagnostics with any credentials removed.</summary>
        public string Endpoint => $"{_serverUrl}/session/{_sessionId}/screenshot";

        /// <summary>
        /// The session's own capabilities, or null when the server will not report them.
        ///
        /// This is where the device facts come from. Tosca does not set test configuration parameters
        /// for screen size, orientation or OS version, so without this the comparison tag went out
        /// empty — and the hub knows all of it, because it allocated the device. The keys are the same
        /// ones every other App Percy SDK reads off its driver (deviceScreenSize, viewportRect,
        /// platformVersion, deviceName), so the metadata layer needs no special case for Tosca.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? TryGetCapabilities()
        {
            // BrowserStack does answer this — it shows as "Retrieve session capabilities" in the App
            // Automate log — and the reply carries everything needed in one go: deviceScreenSize,
            // viewportRect, platformVersion and the device name.
            JsonElement? parsed = Json.TryParse(Get($"{_serverUrl}/session/{_sessionId}", "capabilities"));
            if (parsed == null) return null;

            // Three envelopes, because which one arrives depends on the server: the JSON Wire Protocol
            // wraps the map in "value", W3C new-session replies nest it under "value"."capabilities",
            // and some servers return the map bare. Requiring one of them is what made a working
            // endpoint look like a failing one.
            IReadOnlyDictionary<string, object?>? capabilities =
                Capabilities.AsDictionary(Json.Property(parsed, "value") ?? parsed);
            if (capabilities == null) return null;

            if (capabilities.TryGetValue("capabilities", out object? nested) &&
                Capabilities.AsDictionary(nested) is IReadOnlyDictionary<string, object?> inner)
            {
                capabilities = inner;
            }

            // An empty map is an answer with nothing in it; null says that plainly, and saves the caller
            // reporting that it read zero capabilities.
            return capabilities.Count == 0 ? null : capabilities;
        }

        /// <summary>
        /// The status bar and navigation bar heights, or null.
        ///
        /// Appium's documented route for this — the other App Percy SDKs get the same numbers out of a
        /// <c>viewportRect</c> capability, which needs a driver to read. Without these the comparison
        /// tag reports no bars, nothing is cropped, and the clock in the status bar makes every run
        /// differ.
        /// </summary>
        public (int StatusBar, int NavigationBar)? TryGetSystemBars()
        {
            JsonElement? value = Json.Property(Json.TryParse(
                Get($"{_serverUrl}/session/{_sessionId}/appium/device/system_bars", "system bars")),
                "value");
            if (value == null) return null;

            int? statusBar = HeightOf(value, "statusBar");
            int? navigationBar = HeightOf(value, "navigationBar");
            if (statusBar == null && navigationBar == null) return null;

            return (statusBar ?? 0, navigationBar ?? 0);
        }

        private static int? HeightOf(JsonElement? bars, string name) =>
            Capabilities.ToInt(Json.PropertyAsString(Json.Property(bars, name), "height"));

        /// <summary>
        /// The viewport rectangle, via Appium's <c>mobile: viewportRect</c> extension. This is what the
        /// other SDKs use on iOS, and it gives the usable area directly rather than by subtraction.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? TryGetViewportRect()
        {
            string? result = ExecuteScript("mobile: viewportRect");
            JsonElement? parsed = Json.TryParse(result);
            return parsed == null ? null : Capabilities.AsDictionary(parsed.Value);
        }

        /// <summary>The window's width and height, or null unless the session reports both.</summary>
        public (int Width, int Height)? TryGetWindowSize()
        {
            (int? Width, int? Height) window = Window();
            return window.Width > 0 && window.Height > 0
                ? (window.Width!.Value, window.Height!.Value)
                : null;
        }

        /// <summary>
        /// The device's current orientation, or null. Asked for separately because it is a live fact
        /// rather than a capability — a test can rotate the device mid-run.
        /// </summary>
        public string? TryGetOrientation()
        {
            string? body = Get($"{_serverUrl}/session/{_sessionId}/orientation", "orientation");
            JsonElement? value = Json.Property(Json.TryParse(body), "value");
            return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
        }

        /// <summary>
        /// The session window's width, or null. Only iOS uses it, to work out the scale factor between
        /// the logical window and the real screen.
        /// </summary>
        public int? TryGetWindowWidth()
        {
            int? width = Window().Width;
            return width > 0 ? width : null;
        }

        private (int? Width, int? Height)? _window;

        /// <summary>
        /// Asks the session for its window, once.
        ///
        /// Memoized because two callers want different parts of the same answer — the metadata layer
        /// wants the full size, iOS's scale factor wants only the width — and each was making its own
        /// round trip to a possibly-remote hub, twice over when the first endpoint spelling 404s. The
        /// window does not change within a snapshot, so asking twice bought nothing but latency.
        ///
        /// Width and height are kept separately rather than as a size, because a server that answers
        /// with a width and no height still has something worth having for the scale factor. Deciding
        /// what counts as a usable answer is left to the two callers above.
        /// </summary>
        private (int? Width, int? Height) Window()
        {
            if (_window != null) return _window.Value;

            // W3C moved this to /window/rect; the older spelling is tried second.
            foreach (string path in new[] { "window/rect", "window/current/size" })
            {
                JsonElement? value = Json.Property(
                    Json.TryParse(Get($"{_serverUrl}/session/{_sessionId}/{path}", "window size")),
                    "value");
                if (value == null) continue;

                int? width = Capabilities.ToInt(Json.PropertyAsString(value, "width"));
                int? height = Capabilities.ToInt(Json.PropertyAsString(value, "height"));

                // Either dimension on its own is an answer; a reply with neither is the server saying
                // it has no window to report, so the other spelling is still worth a try.
                if (width > 0 || height > 0) return (_window = (width, height)).Value;
            }
            return (_window = (null, null)).Value;
        }

        /// <summary>
        /// A GET whose failure is a logged null. Every caller has a fallback — a capability the module
        /// can supply instead — so a session that will not answer degrades the tag rather than the run.
        /// </summary>
        private string? Get(string url, string what)
        {
            try
            {
                Task<HttpResponseMessage> request = _http.GetAsync(url);
                request.Wait();
                HttpResponseMessage response = request.Result;

                Task<string> body = response.Content.ReadAsStringAsync();
                body.Wait();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Log($"The device session would not report its {what} " +
                        $"({(int)response.StatusCode}): {Truncate(body.Result)}", "debug");
                    return null;
                }
                return body.Result;
            }
            catch (Exception e)
            {
                Utils.Log($"Could not read the device session's {what}: " +
                    Utils.RedactCredentials(e.Message), "debug");
                return null;
            }
        }

        /// <summary>
        /// Runs a script in the session and returns its result, or null when the server would not run
        /// it.
        ///
        /// This is what makes App Automate's own capture reachable. Its percyScreenshot command travels
        /// as a <c>browserstack_executor:</c> script, and for a long time this SDK treated scripting as
        /// impossible because Tosca will not pass raw Appium commands through. That was the wrong
        /// conclusion: the constraint is Tosca's, not the protocol's, and the hub takes scripts over the
        /// same HTTP route the screenshot uses.
        ///
        /// Both endpoint spellings are tried — W3C moved execute to /execute/sync — because which one a
        /// hub answers is its choice.
        /// </summary>
        public string? ExecuteScript(string script)
        {
            string body = PercyPayload.PayloadParser(new Dictionary<string, object?>
            {
                ["script"] = script,
                ["args"] = Array.Empty<object>()
            });

            foreach (string path in new[] { "execute/sync", "execute" })
            {
                (string? Value, bool WorthTryingAnother) result = Post(path, body);
                if (result.Value != null) return result.Value;
                if (!result.WorthTryingAnother) return null;
            }
            return null;
        }

        /// <summary>
        /// POSTs to one execute endpoint. Reports whether the *other* spelling is worth trying: a 404
        /// means this hub does not have this route, whereas any other failure is about the script and
        /// would fail identically on the other one.
        /// </summary>
        private (string? Value, bool WorthTryingAnother) Post(string path, string body)
        {
            string url = $"{_serverUrl}/session/{_sessionId}/{path}";
            try
            {
                using StringContent content = new StringContent(body, Encoding.UTF8, "application/json");
                Task<HttpResponseMessage> request = _http.PostAsync(url, content);
                request.Wait();
                HttpResponseMessage response = request.Result;

                Task<string> responseBody = response.Content.ReadAsStringAsync();
                responseBody.Wait();

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Utils.Log($"The session has no {path} endpoint; trying the other spelling.",
                            "debug");
                        return (null, true);
                    }
                    Utils.Log($"The device session refused a script ({(int)response.StatusCode} " +
                        $"{response.ReasonPhrase}): {Truncate(responseBody.Result)}");
                    return (null, false);
                }

                // The result may be a string, an object or a number depending on the command, so the
                // raw JSON text is returned and left for the caller to interpret.
                JsonElement? parsed = Json.TryParse(responseBody.Result);
                JsonElement? value = Json.Property(parsed, "value");
                if (value == null) return (null, false);

                return (value.Value.ValueKind == JsonValueKind.String
                    ? value.Value.GetString()
                    : value.Value.GetRawText(), false);
            }
            catch (Exception e)
            {
                Utils.Log("Could not run a script on the device session: " +
                    Utils.RedactCredentials(e.Message));
                Utils.Log(Utils.RedactCredentials(e.ToString()), "debug");
                return (null, false);
            }
        }

        /// <summary>
        /// Fetches the current screen as base64 PNG, or null when the server would not provide one.
        ///
        /// Returns null rather than throwing so the caller can fall back to another capture route; the
        /// reason is always logged, because a 404 here (session gone) and a 500 (device unreachable)
        /// need very different responses from whoever is reading.
        /// </summary>
        public string? TryGetScreenshotBase64()
        {
            for (int attempt = 1; ; attempt++)
            {
                (string? Image, bool WorthRetrying) result = AttemptScreenshot();
                if (result.Image != null) return result.Image;

                if (!result.WorthRetrying || attempt >= Attempts) return null;

                Utils.Log($"Retrying the screenshot ({attempt} of {Attempts}) — the device may not be " +
                    "ready yet.", "debug");
                Sleep(RetryDelay);
            }
        }

        /// <summary>
        /// One attempt. Reports whether retrying could plausibly help, which a bare null could not:
        /// a 4xx means the session or endpoint is wrong and will stay wrong, while a transport failure
        /// or a 5xx is exactly the "not ready yet" case worth a second look.
        /// </summary>
        private (string? Image, bool WorthRetrying) AttemptScreenshot()
        {
            try
            {
                Task<HttpResponseMessage> request = _http.GetAsync(Endpoint);
                request.Wait();
                HttpResponseMessage response = request.Result;

                Task<string> body = response.Content.ReadAsStringAsync();
                body.Wait();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Log($"The device session refused a screenshot ({(int)response.StatusCode} " +
                        $"{response.ReasonPhrase}): {Truncate(body.Result)}");
                    // A 5xx may pass; a 4xx says the session or endpoint is wrong and will not.
                    return (null, (int)response.StatusCode >= 500);
                }

                string? base64 = ExtractValue(body.Result);
                if (string.IsNullOrWhiteSpace(base64))
                {
                    Utils.Log("The device session returned a screenshot response with no image in it: " +
                        Truncate(body.Result));
                    // A well-formed refusal, not a hiccup.
                    return (null, false);
                }
                return (base64, false);
            }
            catch (Exception e)
            {
                // Redacted: the endpoint commonly carries credentials in its userinfo.
                Utils.Log("Could not reach the device session for a screenshot: " +
                    Utils.RedactCredentials(e.Message));
                Utils.Log(Utils.RedactCredentials(e.ToString()), "debug");
                return (null, true);
            }
        }

        /// <summary>
        /// Pulls the image out of either protocol's response shape: W3C returns
        /// <c>{"value": "&lt;base64&gt;"}</c> and the older JSON wire protocol
        /// <c>{"status": 0, "value": "&lt;base64&gt;"}</c>. Both are accepted because which one a
        /// session speaks depends on the server, not on anything decided here.
        /// </summary>
        internal static string? ExtractValue(string? body)
        {
            JsonElement? parsed = Json.TryParse(body);
            if (parsed == null) return null;

            JsonElement? value = Json.Property(parsed, "value");
            if (value == null) return null;

            // A W3C error is reported as an object under `value`, not a string — so anything
            // non-string here is a failure rather than an image.
            return value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
        }

        private static string Truncate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(empty)";
            string redacted = Utils.RedactCredentials(text.Trim());
            // A screenshot response is megabytes of base64; a failure body is short. Cap it either
            // way so a log line stays a log line.
            return redacted.Length <= 300 ? redacted : redacted.Substring(0, 300) + "…";
        }
    }
}
