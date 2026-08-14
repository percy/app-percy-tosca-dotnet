using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// Talks to the automation server directly over plain HTTP, using only the session id and the
    /// server address. Standard WebDriver, no Tricentis API, and fully testable — the
    /// <see cref="HttpClient"/> is injected.
    public class WebDriverSession
    {
        /// A Tosca step can hand over before the thing it steered is ready. Kept low because each
        /// attempt is a real round trip to a possibly-remote hub.
        public const int Attempts = 3;

        /// Gap between attempts.
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        /// Replaceable so tests do not actually wait.
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

        /// The screenshot endpoint, exposed for diagnostics with any credentials removed.
        public string Endpoint => $"{_serverUrl}/session/{_sessionId}/screenshot";

        /// Where the device facts come from: Tosca sets no parameters for screen size, orientation or
        /// OS version, and the hub knows all of it because it allocated the device. The keys match what
        /// the other App Percy SDKs read off their driver, so the metadata layer needs no special case.
        public IReadOnlyDictionary<string, object?>? TryGetCapabilities()
        {
            JsonElement? parsed = Json.TryParse(Get($"{_serverUrl}/session/{_sessionId}", "capabilities"));
            if (parsed == null) return null;

            // Three envelopes, server's choice: "value", "value"."capabilities", or the map bare.
            IReadOnlyDictionary<string, object?>? capabilities =
                Capabilities.AsDictionary(Json.Property(parsed, "value") ?? parsed);
            if (capabilities == null) return null;

            if (capabilities.TryGetValue("capabilities", out object? nested) &&
                Capabilities.AsDictionary(nested) is IReadOnlyDictionary<string, object?> inner)
            {
                capabilities = inner;
            }

            // An empty map is an answer with nothing in it; null says so, and spares the caller
            // reporting that it read zero capabilities.
            return capabilities.Count == 0 ? null : capabilities;
        }

        /// Bar heights, or null. Without them nothing is cropped and the status bar clock makes every
        /// run differ. The other SDKs read the same numbers off a <c>viewportRect</c> capability.
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

        /// The usable area stated directly, rather than derived by subtracting the bars.
        public IReadOnlyDictionary<string, object?>? TryGetViewportRect()
        {
            string? result = ExecuteScript("mobile: viewportRect");
            JsonElement? parsed = Json.TryParse(result);
            return parsed == null ? null : Capabilities.AsDictionary(parsed.Value);
        }

        /// The window's width and height, or null unless the session reports both.
        public (int Width, int Height)? TryGetWindowSize()
        {
            (int? Width, int? Height) window = Window();
            return window.Width > 0 && window.Height > 0
                ? (window.Width!.Value, window.Height!.Value)
                : null;
        }

        /// Asked separately from the capabilities: a test can rotate the device mid-run.
        public string? TryGetOrientation()
        {
            string? body = Get($"{_serverUrl}/session/{_sessionId}/orientation", "orientation");
            JsonElement? value = Json.Property(Json.TryParse(body), "value");
            return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
        }

        /// Only iOS uses this, for the scale factor between the logical and real screen.
        public int? TryGetWindowWidth()
        {
            int? width = Window().Width;
            return width > 0 ? width : null;
        }

        private (int? Width, int? Height)? _window;

        /// Asks the session for its window, once. Two callers want different parts of the same answer
        /// and it cannot change within a snapshot, so a second round trip buys only latency.
        ///
        /// Width and height are kept separately: a width with no height is still worth having for the
        /// scale factor, so what counts as usable is left to each caller.
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

                // Neither dimension means no window to report, so the other spelling is worth a try.
                if (width > 0 || height > 0) return (_window = (width, height)).Value;
            }
            return (_window = (null, null)).Value;
        }

        /// Failure is a logged null: a session that will not answer degrades the tag, not the run.
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

        /// The device App Automate actually allocated, or null.
        ///
        /// The one BrowserStack-specific call in this SDK, and it earns the exception. A `deviceName`
        /// capability is what the client asked for, not what it got: Tosca pins the device with `udid`
        /// and sends a `deviceName` from its process-wide configuration, so with several test cases in
        /// flight the two disagree — on one measured session BrowserStack allocated an iPhone 14 while
        /// the capability said "iPhone 14 Pro Max". Screen size and OS version are measured from the
        /// real device, which is why only the name came out wrong, and Percy groups baselines by that
        /// name, so a wrong one merges devices that should be compared separately.
        ///
        /// Nothing in the capability bag can settle it: on iOS there is no `deviceModel`, and `device`
        /// is the family ("iphone"). The REST API is the only source that names the hardware.
        ///
        /// Authenticated with the credentials already in the hub URL's userinfo, so there is nothing new
        /// to configure. Without them the caller keeps whatever the capability said.
        public string? TryGetAllocatedDeviceName()
        {
            string? credentials = HubCredentials();
            if (credentials == null)
            {
                Utils.Log("The AppiumServer URL carries no credentials, so App Automate cannot be asked " +
                    "which device it allocated. The session's deviceName is used as-is, which is what it " +
                    "was asked for rather than what it got.", "debug");
                return null;
            }

            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api-cloud.browserstack.com/app-automate/sessions/{_sessionId}.json");
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));

                Task<HttpResponseMessage> send = _http.SendAsync(request);
                send.Wait();
                using HttpResponseMessage response = send.Result;

                Task<string> body = response.Content.ReadAsStringAsync();
                body.Wait();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Log($"App Automate would not say which device it allocated " +
                        $"({(int)response.StatusCode}): {Truncate(body.Result)}", "debug");
                    return null;
                }

                string? device = Json.PropertyAsString(
                    Json.Property(Json.TryParse(body.Result), "automation_session"), "device");
                return string.IsNullOrWhiteSpace(device) ? null : device.Trim();
            }
            catch (Exception e)
            {
                // Warn, not debug: the tag falls back to the name the client asked for, and if that
                // disagrees with the hardware the baselines merge silently — which is the whole reason
                // for this call.
                Utils.Log("Could not ask App Automate which device it allocated, so this snapshot is " +
                    "tagged with the name the session was asked for: " +
                    Utils.RedactCredentials(e.Message), "warn");
                return null;
            }
        }

        /// <c>user:key</c> from the hub URL, or null when it carries none.
        private string? HubCredentials()
        {
            if (!Uri.TryCreate(_serverUrl, UriKind.Absolute, out Uri? hub)) return null;

            string credentials = Uri.UnescapeDataString(hub.UserInfo);
            return credentials.Contains(':', StringComparison.Ordinal) ? credentials : null;
        }

        /// Runs a script in the session. This is what carries the <c>browserstack_executor:</c>
        /// percyScreenshot commands, and therefore what makes App Automate's own capture reachable.
        ///
        /// Both spellings are tried — W3C moved execute to /execute/sync — since it is the hub's choice.
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

        /// POSTs to one execute endpoint. A 404 means this hub lacks the route, so the other spelling
        /// is worth trying; any other failure is about the script and would repeat.
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

                // A string, object or number depending on the command; the caller interprets it.
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

        /// The current screen as base64 PNG, or null. The reason is always logged: a 404 (session gone)
        /// and a 500 (device unreachable) need very different responses from whoever is reading.
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

        /// One attempt, and whether retrying could help: a 4xx will stay wrong, while a transport
        /// failure or a 5xx is the "not ready yet" case worth another look.
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

        /// Accepts either protocol's shape — W3C's <c>{"value": ...}</c> and the JSON wire protocol's
        /// <c>{"status": 0, "value": ...}</c> — since which one a session speaks is the server's choice.
        internal static string? ExtractValue(string? body)
        {
            JsonElement? parsed = Json.TryParse(body);
            if (parsed == null) return null;

            JsonElement? value = Json.Property(parsed, "value");
            if (value == null) return null;

            // A W3C error arrives as an object under `value`, so non-string is a failure, not an image.
            return value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
        }

        private static string Truncate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(empty)";
            string redacted = Utils.RedactCredentials(text.Trim());
            // A screenshot response is megabytes of base64; cap it so a log line stays a log line.
            return redacted.Length <= 300 ? redacted : redacted.Substring(0, 300) + "…";
        }
    }
}
