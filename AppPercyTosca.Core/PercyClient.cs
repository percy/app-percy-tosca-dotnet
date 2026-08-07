using System.Text;
using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Tosca-free HTTP client for the Percy CLI. The <see cref="HttpClient"/> (and therefore its
    /// <see cref="HttpMessageHandler"/>) is injected so every endpoint can be exercised in tests
    /// with no network access.
    /// </summary>
    public class PercyClient
    {
        /// <summary>/percy/comparison, which App Percy needs, landed in CLI 1.27.</summary>
        public const int MinimumMinorVersion = 27;

        /// <summary>
        /// How long a healthcheck answer is trusted. The other SDKs memoize for the life of the
        /// process, which suits them — the process is one test run. Commander stays open for days
        /// across many `percy app:exec:start` cycles, so a permanent memo means running a sheet before
        /// starting Percy disables every later run until Commander is restarted.
        /// </summary>
        public static readonly TimeSpan HealthcheckTtl = TimeSpan.FromSeconds(60);

        /// <summary>Clock, replaceable so the expiry can be tested without waiting a minute.</summary>
        internal static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        private readonly HttpClient _http;
        private readonly string _cliApi;
        private bool? _enabled;
        private DateTime _checkedAt;

        public PercyClient(HttpClient http, string? cliApi = null)
        {
            _http = http;
            _cliApi = (cliApi ?? Env.CliApi()).TrimEnd('/');
        }

        /// <summary>
        /// Performs a Percy CLI request: a POST with a JSON body when <paramref name="payload"/>
        /// is non-null, otherwise a GET. Throws on a non-success status code, and returns the body
        /// plus the x-percy-core-version header.
        /// </summary>
        public PercyResponse Request(string endpoint, object? payload = null, bool isJson = false)
        {
            StringContent? body = payload == null ? null : new StringContent(
                PercyPayload.PayloadParser(payload, isJson), Encoding.UTF8, "application/json");

            Task<HttpResponseMessage> apiTask = body != null
                ? _http.PostAsync($"{_cliApi}{endpoint}", body)
                : _http.GetAsync($"{_cliApi}{endpoint}");
            apiTask.Wait();

            HttpResponseMessage response = apiTask.Result;
            response.EnsureSuccessStatusCode();

            Task<string> contentTask = response.Content.ReadAsStringAsync();
            contentTask.Wait();

            string? version = response.Headers.TryGetValues("x-percy-core-version",
                out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null;

            return new PercyResponse(version, contentTask.Result);
        }

        /// <summary>
        /// Whether Percy is running and new enough, memoized for <see cref="HealthcheckTtl"/>. Also
        /// records the build and session type, so an expiry picks up a CLI that has been restarted —
        /// including into a mode this SDK does not support.
        /// </summary>
        /// <remarks>
        /// Unlocked on purpose. The race is benign: two concurrent steps both healthcheck and write the
        /// same answer. A lock would be worse — <see cref="RunHealthcheck"/> blocks on HTTP, so one
        /// step with an unresponsive CLI would stall every step behind it.
        /// </remarks>
        public bool Healthcheck()
        {
            if (_enabled != null && Now() - _checkedAt < HealthcheckTtl) return _enabled.Value;

            // Before the call, not after: stamping after extends the window by the request duration.
            _checkedAt = Now();
            return (_enabled = RunHealthcheck()).Value;
        }

        private bool RunHealthcheck()
        {
            try
            {
                PercyResponse res = Request("/percy/healthcheck");
                JsonElement? data = Json.TryParse(res.Content);

                if (!Json.IsTrue(data, "success"))
                {
                    throw new Exception(Json.PropertyAsString(data, "error") ?? "Percy healthcheck failed");
                }

                JsonElement? build = Json.Property(data, "build");
                Env.PercyBuildId = Json.PropertyAsString(build, "id");
                Env.PercyBuildUrl = Json.PropertyAsString(build, "url");
                Env.SessionType = Json.PropertyAsString(data, "type");

                if (res.Version == null)
                {
                    Utils.Log("You may be using @percy/agent " +
                        "which is no longer supported by this SDK. " +
                        "Please uninstall @percy/agent and install @percy/cli instead. " +
                        "https://www.browserstack.com/docs/percy/migration/migrate-to-cli");
                    return false;
                }

                return VersionSupported(res.Version);
            }
            catch (Exception error)
            {
                Utils.Log("Percy is not running, disabling snapshots");
                Utils.Log(error.Message);
                Utils.Log(error.ToString(), "debug");
                return false;
            }
        }

        /// <summary>
        /// The version gate. An unparseable version is refused rather than assumed good: posting to a
        /// CLI without /percy/comparison fails every snapshot with a less obvious error than this.
        /// </summary>
        public static bool VersionSupported(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                Utils.Log("Could not determine Percy CLI version, disabling snapshots");
                return false;
            }

            string[] parts = version.Split('.');
            if (!int.TryParse(parts[0], out int major))
            {
                Utils.Log($"Could not parse Percy CLI version, {version}");
                return false;
            }
            if (major < 1)
            {
                Utils.Log($"Unsupported Percy CLI version, {version}");
                return false;
            }
            // Past the gate, so the minor is irrelevant — reading it would refuse "2.0" for 0 < 27.
            if (major > 1) return true;

            // "1" with no minor cannot be shown to meet the 1.27 gate.
            int minor = parts.Length > 1 && int.TryParse(parts[1], out int parsedMinor) ? parsedMinor : -1;
            if (minor < MinimumMinorVersion)
            {
                Utils.Log($"Percy CLI version, {version} is not minimum version required, " +
                    $"App Percy is available from 1.{MinimumMinorVersion}.0.", "warn");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Posts a captured screenshot, or null when the CLI refused. Logged rather than thrown: a
        /// visual snapshot must not fail an otherwise-passing Tosca step.
        /// </summary>
        public JsonElement? PostScreenshot(
            string name,
            Dictionary<string, object?> tag,
            List<Tile> tiles,
            string? externalDebugUrl,
            object? ignoredElementsData,
            object? consideredElementsData,
            ScreenshotOptions options)
        {
            try
            {
                Dictionary<string, object?> payload = new Dictionary<string, object?>
                {
                    ["clientInfo"] = Env.ClientInfo,
                    ["environmentInfo"] = Env.EnvironmentInfo,
                    ["tag"] = tag,
                    ["tiles"] = Tile.ToPayload(tiles),
                    ["externalDebugUrl"] = externalDebugUrl,
                    ["name"] = name,
                    ["ignoredElementsData"] = ignoredElementsData,
                    ["consideredElementsData"] = consideredElementsData,
                    ["labels"] = options.Labels
                };

                // TEMP diagnostics — remove before release. PayloadParser is the same serializer
                // Request uses, so what is logged is byte-for-byte what goes on the wire.
                Utils.Log($"TEMP PostScreenshot label={options.Labels ?? "<null>"}");
                Utils.Log($"TEMP PostScreenshot payload={PercyPayload.PayloadParser(payload)}");

                return Post("/percy/comparison", payload, name);
            }
            catch (Exception error)
            {
                Utils.Log($"Could not take screenshot \"{name}\"");
                Utils.Log(error.ToString(), "debug");
                return null;
            }
        }

        /// <summary>Best-effort: puts an SDK failure in the build, not only in a log on one workstation.</summary>
        public void PostFailedEvent(string message)
        {
            try
            {
                Dictionary<string, object?> payload = new Dictionary<string, object?>
                {
                    ["clientInfo"] = Env.ClientInfo,
                    ["message"] = message,
                    ["errorKind"] = "sdk"
                };
                PercyResponse res = Request("/percy/events", payload);
                JsonElement? data = Json.TryParse(res.Content);
                if (!Json.IsTrue(data, "success"))
                {
                    throw new Exception(Json.PropertyAsString(data, "error") ?? "unknown error");
                }
            }
            catch (Exception error)
            {
                Utils.Log("Could not send failed event", "debug");
                Utils.Log(error.ToString(), "debug");
            }
        }

        /// <summary>Forwards a log line to the CLI so it appears interleaved in Percy's output.</summary>
        public void PostLog(string message, string level = "info")
        {
            Request("/percy/log", new Dictionary<string, object?>
            {
                ["message"] = message,
                ["level"] = level
            });
        }

        /// <summary>
        /// Returns the whole response, not its `data` member: the CLI replies `{ success, link, data }`
        /// and `link` is a sibling of `data`, which the App Automate flow needs to report the
        /// comparison URL back to the session log.
        ///
        /// success:false is an error, not an empty result — its message is the only explanation of why
        /// the snapshot did not appear in the build.
        /// </summary>
        private JsonElement? Post(string endpoint, Dictionary<string, object?> payload, string name)
        {
            PercyResponse res = Request(endpoint, payload);
            JsonElement? response = Json.TryParse(res.Content);
            if (!Json.IsTrue(response, "success"))
            {
                throw new Exception(Json.PropertyAsString(response, "error")
                    ?? $"Percy CLI rejected screenshot \"{name}\"");
            }
            return response;
        }
    }
}
