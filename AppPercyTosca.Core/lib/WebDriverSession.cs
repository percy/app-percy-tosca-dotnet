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
