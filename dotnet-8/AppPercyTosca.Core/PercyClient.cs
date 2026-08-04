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
        /// <summary>
        /// App Percy needs a CLI that can serve /percy/comparison and, for Percy on Automate,
        /// /percy/automateScreenshot. Both landed in 1.27.
        /// </summary>
        public const int MinimumMinorVersion = 27;

        private readonly HttpClient _http;
        private readonly string _cliApi;
        private bool? _enabled;

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
        /// Whether Percy is running and new enough, memoized for the process. Also records the
        /// build id/url and session type the CLI reports, which the App Automate flow stamps onto
        /// its executor calls.
        /// </summary>
        public bool Healthcheck()
        {
            if (_enabled != null) return _enabled.Value;
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
        /// Applies the version gate to a raw x-percy-core-version value. Anything that cannot be
        /// parsed is refused rather than assumed good — an unreadable version means we cannot
        /// know whether /percy/comparison exists, and posting to a CLI without it would fail
        /// every snapshot with a less obvious error than this one.
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
            // A major above 1 is newer than the gate, so the minor is irrelevant. Reading it
            // anyway would refuse a hypothetical "2.0" for having minor 0 < 27.
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
        /// Posts a captured App Percy screenshot (the tile flow) and returns the response's `data`
        /// object, or null when the CLI refused it. Failures are logged rather than thrown: a
        /// visual snapshot must not fail an otherwise-passing Tosca test step.
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
                    ["sync"] = options.Sync,
                    ["testCase"] = options.TestCase,
                    ["labels"] = options.Labels,
                    ["thTestCaseExecutionId"] = options.ThTestCaseExecutionId
                };
                return PostAndUnwrap("/percy/comparison", payload, name);
            }
            catch (Exception error)
            {
                Utils.Log($"Could not take screenshot \"{name}\"");
                Utils.Log(error.ToString(), "debug");
                return null;
            }
        }

        /// <summary>
        /// Posts a Percy on Automate screenshot: the CLI reconnects to the session itself and
        /// captures server-side, so no image data crosses this boundary.
        /// </summary>
        public JsonElement? PostAutomateScreenshot(
            string name,
            string sessionId,
            string? commandExecutorUrl,
            IReadOnlyDictionary<string, object?> capabilities,
            Dictionary<string, object?> options)
        {
            try
            {
                Dictionary<string, object?> payload = new Dictionary<string, object?>
                {
                    ["clientInfo"] = Env.ClientInfo,
                    ["environmentInfo"] = Env.EnvironmentInfo,
                    ["sessionId"] = sessionId,
                    ["commandExecutorUrl"] = commandExecutorUrl,
                    ["capabilities"] = capabilities,
                    ["snapshotName"] = name,
                    ["options"] = options
                };
                return PostAndUnwrap("/percy/automateScreenshot", payload, name);
            }
            catch (Exception error)
            {
                Utils.Log($"Could not take screenshot \"{name}\"");
                Utils.Log(error.ToString(), "debug");
                return null;
            }
        }

        /// <summary>
        /// Reports an SDK-side failure to Percy so it shows up in the build rather than only in a
        /// Tosca log on someone's workstation. Best-effort by design.
        /// </summary>
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
        /// POSTs a screenshot payload and returns its `data` member. A CLI that answers
        /// success:false is an error, not an empty result — the message it gives is the only
        /// explanation of why the snapshot did not appear in the build.
        /// </summary>
        private JsonElement? PostAndUnwrap(string endpoint, Dictionary<string, object?> payload, string name)
        {
            PercyResponse res = Request(endpoint, payload);
            JsonElement? data = Json.TryParse(res.Content);
            if (!Json.IsTrue(data, "success"))
            {
                throw new Exception(Json.PropertyAsString(data, "error")
                    ?? $"Percy CLI rejected screenshot \"{name}\"");
            }
            return Json.Property(data, "data");
        }
    }
}
