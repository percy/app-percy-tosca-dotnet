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
        /// Fetches the current screen as base64 PNG, or null when the server would not provide one.
        ///
        /// Returns null rather than throwing so the caller can fall back to another capture route; the
        /// reason is always logged, because a 404 here (session gone) and a 500 (device unreachable)
        /// need very different responses from whoever is reading.
        /// </summary>
        public string? TryGetScreenshotBase64()
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
                    return null;
                }

                string? base64 = ExtractValue(body.Result);
                if (string.IsNullOrWhiteSpace(base64))
                {
                    Utils.Log("The device session returned a screenshot response with no image in it: " +
                        Truncate(body.Result));
                    return null;
                }
                return base64;
            }
            catch (Exception e)
            {
                // Redacted: the endpoint commonly carries credentials in its userinfo.
                Utils.Log("Could not reach the device session for a screenshot: " +
                    Utils.RedactCredentials(e.Message));
                Utils.Log(Utils.RedactCredentials(e.ToString()), "debug");
                return null;
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
