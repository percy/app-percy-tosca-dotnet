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

        /// <summary>What the session under test should look like, from the test configuration.</summary>
        public class Hints
        {
            public string? DeviceName { get; init; }
            public string? OsVersion { get; init; }
            public string? App { get; init; }

            public bool Any => !string.IsNullOrWhiteSpace(DeviceName)
                || !string.IsNullOrWhiteSpace(OsVersion) || !string.IsNullOrWhiteSpace(App);

            public override string ToString() => string.Join(" ", new[]
            {
                DeviceName, OsVersion, App
            }.Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        /// <summary>One session as BrowserStack describes it.</summary>
        internal class Session
        {
            public string Id { get; init; } = "";
            public string? Status { get; init; }
            public string? Device { get; init; }
            public string? OsVersion { get; init; }
            public string? App { get; init; }

            public bool Running =>
                Status?.IndexOf("running", StringComparison.OrdinalIgnoreCase) >= 0;

            public override string ToString() =>
                $"{Id} ({string.Join(" ", new[] { Device, OsVersion }.Where(v => v != null))})";
        }

        /// <summary>
        /// Returns the session id of the App Automate session under test, or null.
        ///
        /// Several sessions running on one account is the normal case for a shared BrowserStack
        /// account, so "the one that is running" is not on its own an answer. The test configuration
        /// already says which device and OS this test asked for, and BrowserStack reports the same for
        /// each session — so those narrow the field, and only genuine ambiguity after narrowing is
        /// refused.
        /// </summary>
        public string? TryFindSessionId(string? hubUrl, Hints? hints = null)
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
                List<string> buildIds = CandidateBuildIds(Get("/builds.json?limit=10", credentials.Value));
                if (buildIds.Count == 0)
                {
                    Utils.Log("BrowserStack reported no App Automate builds for this account, so the " +
                        "session id could not be discovered.");
                    return null;
                }

                // Every candidate build is searched, not just one. With several builds in flight,
                // picking a build first and then refusing on its sessions would refuse before ever
                // looking at the session that matches this test.
                List<Session> sessions = new List<Session>();
                foreach (string buildId in buildIds)
                {
                    sessions.AddRange(
                        SessionsIn(Get($"/builds/{buildId}/sessions.json?limit=10", credentials.Value)));
                }

                // By id: searching several builds can surface the same session twice, and a duplicate
                // is not ambiguity.
                sessions = sessions.GroupBy(x => x.Id).Select(g => g.First()).ToList();

                List<Session> running = sessions.Where(x => x.Running).ToList();
                // Falling back to all of them because a session flips to "done" between the app
                // finishing and this being asked.
                List<Session> candidates = running.Count > 0 ? running : sessions;

                if (candidates.Count == 0)
                {
                    Utils.Log($"BrowserStack reported no sessions across {buildIds.Count} build(s).");
                    return null;
                }

                return Choose(candidates, hints);
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
        /// Narrows the candidates by what the test configuration asked for, and returns the single
        /// survivor.
        ///
        /// Refusing when several survive is the point. Picking one would capture whichever session
        /// happened to be listed first, and a snapshot of the wrong device is worse than no snapshot:
        /// it looks like a real result and would be accepted as a baseline. The other App Percy SDKs
        /// never face this because their driver knows its own session.
        /// </summary>
        private static string? Choose(List<Session> candidates, Hints? hints)
        {
            List<Session> narrowed = candidates;

            if (hints != null && hints.Any && candidates.Count > 1)
            {
                List<Session> matching = candidates.Where(c => Matches(c, hints)).ToList();
                if (matching.Count > 0)
                {
                    Utils.Log($"Narrowed {candidates.Count} candidate session(s) to {matching.Count} " +
                        $"matching '{hints}' from the test configuration.", "debug");
                    narrowed = matching;
                }
            }

            if (narrowed.Count == 1)
            {
                Utils.Log($"Using App Automate session {narrowed[0]}, inferred from BrowserStack. If " +
                    "that is not the session under test, supply the id through the 'Get Appium Session " +
                    "Id' module instead.");
                return narrowed[0].Id;
            }

            Utils.Log($"BrowserStack reports {narrowed.Count} candidate sessions " +
                $"({string.Join("; ", narrowed)}) and the test configuration does not distinguish them" +
                (hints != null && hints.Any ? $" (looking for '{hints}')" : "") +
                ". Set DeviceName and OsVersion on the Percy module to match the device under test, " +
                "supply the session id through the 'Get Appium Session Id' module, or run one at a time.");
            return null;
        }

        /// <summary>
        /// Whether a session looks like the one asked for. Every hint that is set must agree; a hint
        /// BrowserStack does not report cannot rule a session out, since absence is not disagreement.
        /// </summary>
        internal static bool Matches(Session session, Hints hints) =>
            Agrees(session.Device, hints.DeviceName)
            && Agrees(session.OsVersion, hints.OsVersion)
            && Agrees(session.App, hints.App);

        private static bool Agrees(string? reported, string? wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted) || string.IsNullOrWhiteSpace(reported)) return true;
            // Loose both ways: "Google Pixel 7" vs "Pixel 7", and "13" vs "13.0".
            return reported.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0
                || wanted.IndexOf(reported, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Build ids worth searching: the running ones, or all of them if none are.</summary>
        internal static List<string> CandidateBuildIds(string? body)
        {
            List<Session> builds = Entries(body, "automation_build");
            List<Session> running = builds.Where(b => b.Running).ToList();
            return (running.Count > 0 ? running : builds).Select(b => b.Id).ToList();
        }

        internal static List<Session> SessionsIn(string? body) => Entries(body, "automation_session");

        /// <summary>
        /// Reads BrowserStack's list responses, which wrap each entry in a single-key object —
        /// [{"automation_build": {...}}].
        /// </summary>
        internal static List<Session> Entries(string? body, string wrapper)
        {
            List<Session> entries = new List<Session>();
            JsonElement? parsed = Json.TryParse(body);
            if (parsed == null || parsed.Value.ValueKind != JsonValueKind.Array) return entries;

            foreach (JsonElement entry in parsed.Value.EnumerateArray())
            {
                JsonElement? inner = Json.Property(entry, wrapper) ?? entry;
                string? id = Json.PropertyAsString(inner, "hashed_id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                entries.Add(new Session
                {
                    Id = id,
                    Status = Json.PropertyAsString(inner, "status"),
                    Device = Json.PropertyAsString(inner, "device"),
                    OsVersion = Json.PropertyAsString(inner, "os_version"),
                    App = Json.PropertyAsString(inner, "app_details")
                        ?? Json.PropertyAsString(inner, "app")
                });
            }
            return entries;
        }
    }
}
