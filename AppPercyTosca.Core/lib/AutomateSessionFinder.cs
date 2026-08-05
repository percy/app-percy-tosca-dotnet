using System.Text;
using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Finds the running App Automate session id by asking BrowserStack, for when Tosca will not tell
    /// us.
    ///
    /// The <c>Get Appium Session Id</c> standard module is the intended source, but it is not always
    /// usable — notably against a cloud connection. Without a session id the device cannot be asked
    /// for a screenshot, and on Tosca 24 there is no mobile screenshot task to fall back to either, so
    /// this closes the only remaining gap.
    ///
    /// It needs nothing Tosca does not already have: BrowserStack's Tosca setup puts the credentials
    /// in the <c>AppiumServer</c> hub URL as userinfo, and the same pair authenticates the REST API.
    ///
    /// One assumption worth stating: a BrowserStack session's <c>hashed_id</c> is also its WebDriver
    /// session id. That holds for Automate and is what makes this work — and if it ever stops holding,
    /// the screenshot request 404s with a message naming the session, rather than failing silently.
    ///
    /// Nothing like this exists in the other App Percy SDKs, and that is worth understanding rather
    /// than glossing: they are handed a driver that knows its own session id, whereas this infers one
    /// from "what is running on this account". Inference can be wrong — with two runs in flight, or a
    /// shared account, the wrong session would be captured and the result would look plausible instead
    /// of failing. So ambiguity is refused rather than guessed: more than one running candidate means
    /// no answer. Supplying the id through the <c>Get Appium Session Id</c> module avoids the guess
    /// altogether and is preferable wherever it is possible.
    /// </summary>
    public class AutomateSessionFinder
    {
        /// <summary>Overridable for a different BrowserStack region or a test double.</summary>
        public const string DefaultApiRoot = "https://api-cloud.browserstack.com/app-automate";

        private readonly HttpClient _http;
        private readonly string _apiRoot;

        public AutomateSessionFinder(HttpClient http, string? apiRoot = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _apiRoot = (apiRoot ?? DefaultApiRoot).TrimEnd('/');
        }

        /// <summary>
        /// Pulls "user" and "key" out of a hub URL's userinfo, or null when it carries none. Returns
        /// the pair rather than a header so the caller can report *which* part is missing.
        /// </summary>
        public static (string User, string Key)? CredentialsFrom(string? hubUrl)
        {
            if (string.IsNullOrWhiteSpace(hubUrl)) return null;

            try
            {
                Uri uri = new Uri(hubUrl);
                if (string.IsNullOrEmpty(uri.UserInfo)) return null;

                string[] parts = uri.UserInfo.Split(new[] { ':' }, 2);
                if (parts.Length != 2) return null;

                string user = Uri.UnescapeDataString(parts[0]);
                string key = Uri.UnescapeDataString(parts[1]);
                return string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(key)
                    ? null
                    : (user, key);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the session id of the running App Automate session, or null. Prefers a session
        /// BrowserStack reports as running; falls back to the most recent, since a session can flip to
        /// "done" between the app finishing and this being asked.
        /// </summary>
        public string? TryFindSessionId(string? hubUrl)
        {
            (string User, string Key)? credentials = CredentialsFrom(hubUrl);
            if (credentials == null)
            {
                Utils.Log("The AppiumServer parameter carries no credentials, so BrowserStack cannot " +
                    "be asked which session is running. Either embed them in the hub URL as " +
                    "https://user:key@hub-cloud.browserstack.com/wd/hub, or supply the session id via " +
                    "the 'Get Appium Session Id' module.");
                return null;
            }

            try
            {
                string builds = Get("/builds.json?limit=5", credentials.Value);
                string? buildId = Unambiguous(HashedIds(builds, "automation_build", running: true), "build")
                    ?? Unambiguous(HashedIds(builds, "automation_build", running: false), "build");
                if (buildId == null)
                {
                    Utils.Log("BrowserStack reported no usable App Automate build for this account, so " +
                        "the session id could not be discovered.");
                    return null;
                }

                string sessions = Get($"/builds/{buildId}/sessions.json?limit=5", credentials.Value);
                string? sessionId =
                    Unambiguous(HashedIds(sessions, "automation_session", running: true), "session")
                    ?? Unambiguous(HashedIds(sessions, "automation_session", running: false), "session");

                if (sessionId == null)
                {
                    Utils.Log($"BrowserStack build {buildId} reported no usable session.");
                    return null;
                }

                Utils.Log($"Using App Automate session {sessionId} (build {buildId}), inferred from " +
                    "BrowserStack as the only one running. If that is not the session under test, " +
                    "supply the id through the 'Get Appium Session Id' module instead.");
                return sessionId;
            }
            catch (Exception e)
            {
                Utils.Log("Could not ask BrowserStack which session is running: " +
                    Utils.RedactCredentials(e.Message));
                Utils.Log(Utils.RedactCredentials(e.ToString()), "debug");
                return null;
            }
        }

        private string Get(string path, (string User, string Key) credentials)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, _apiRoot + path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{credentials.User}:{credentials.Key}")));

            Task<HttpResponseMessage> send = _http.SendAsync(request);
            send.Wait();
            HttpResponseMessage response = send.Result;

            Task<string> body = response.Content.ReadAsStringAsync();
            body.Wait();

            if (!response.IsSuccessStatusCode)
            {
                throw new PercyException(
                    $"BrowserStack returned {(int)response.StatusCode} for {path}. " +
                    "Check the credentials in the AppiumServer parameter.");
            }
            return body.Result;
        }

        /// <summary>
        /// Every hashed_id in one of BrowserStack's list responses, which wrap each entry in a
        /// single-key object — [{"automation_build": {...}}]. With <paramref name="running"/> set, only
        /// entries whose status says they are running are returned; that pass is tried first so a stale
        /// build is never preferred over the live one.
        /// </summary>
        internal static List<string> HashedIds(string? body, string wrapper, bool running)
        {
            List<string> ids = new List<string>();
            JsonElement? parsed = Json.TryParse(body);
            if (parsed == null || parsed.Value.ValueKind != JsonValueKind.Array) return ids;

            foreach (JsonElement entry in parsed.Value.EnumerateArray())
            {
                JsonElement? inner = Json.Property(entry, wrapper) ?? entry;
                string? hashedId = Json.PropertyAsString(inner, "hashed_id");
                if (string.IsNullOrWhiteSpace(hashedId)) continue;

                if (running)
                {
                    string? status = Json.PropertyAsString(inner, "status");
                    if (status == null ||
                        status.IndexOf("running", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }
                ids.Add(hashedId);
            }
            return ids;
        }

        /// <summary>
        /// The single candidate, or null when there are none or several.
        ///
        /// Refusing on several is the point. Picking one would capture whichever session happened to be
        /// listed first, and a snapshot of the wrong device is worse than no snapshot: it looks like a
        /// real result and would be accepted as a baseline. The other App Percy SDKs never face this
        /// because their driver knows its own session.
        /// </summary>
        private static string? Unambiguous(List<string> candidates, string what)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            Utils.Log($"BrowserStack reports {candidates.Count} running {what}s " +
                $"({string.Join(", ", candidates)}), so which one is under test cannot be inferred. " +
                "Supply the session id through the 'Get Appium Session Id' module, or run one at a time.");
            return null;
        }
    }
}
