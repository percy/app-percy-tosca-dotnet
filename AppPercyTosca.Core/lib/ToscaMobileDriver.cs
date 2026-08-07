// Aliased because the IMobileDriver.Capabilities property shadows the Capabilities helper class.
using Caps = AppPercyTosca.Core.Capabilities;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// A device session built from the only two things Tosca exposes: the hub address and the Appium
    /// session id. Everything else is plain WebDriver over HTTP and touches no Tricentis API.
    ///
    /// Querying for elements is the exception, so element-based regions are unavailable.
    /// </summary>
    public class ToscaMobileDriver : IMobileDriver
    {
        /// <summary>TCP holding the hub URL, e.g. https://hub-cloud.browserstack.com/wd/hub.</summary>
        public const string AppiumServerTcp = "AppiumServer";

        /// <summary>
        /// Buffer the `Get Appium Session Id` module is expected to have written to. The user names
        /// that buffer, so the Percy module can override this.
        /// </summary>
        public const string DefaultSessionIdBuffer = "PercyAppiumSessionId";

        /// <summary>
        /// TCP names mapped to capability names. Several spellings each, because the engine's TCP set
        /// differs between connection types.
        /// </summary>
        private static readonly (string Capability, string[] Tcps)[] CapabilityMap =
        {
            ("deviceName", new[] { "DeviceName", "Device", "MobileDeviceName" }),
            ("platformName", new[] { "PlatformName", "MobileOS", "OS", "Platform" }),
            ("platformVersion", new[] { "OSVersion", "PlatformVersion", "MobileOSVersion" }),
            ("orientation", new[] { "Orientation", "DeviceOrientation" }),
            ("deviceScreenSize", new[] { "DeviceScreenSize", "ScreenResolution", "Resolution" }),
            ("app", new[] { "AppPath", "App" }),
            ("udid", new[] { "Udid", "UDID", "DeviceId" })
        };

        private readonly IToscaEnvironment _tosca;
        private readonly Func<string, string, WebDriverSession>? _webDriver;
        private readonly string? _sessionIdBuffer;
        private readonly string? _explicitSessionId;
        private Dictionary<string, object?>? _capabilities;
        private readonly string _fallbackSessionId;

        /// <param name="webDriver">
        /// Builds a session client for a (serverUrl, sessionId) pair; supplied by the shim, which owns
        /// the HttpClient. Null leaves the device unreachable and every fact unanswered.
        /// </param>
        public ToscaMobileDriver(
            IToscaEnvironment tosca,
            string? sessionIdBuffer = null,
            string? fallbackSessionId = null,
            string? explicitSessionId = null,
            Func<string, string, WebDriverSession>? webDriver = null)
        {
            _tosca = tosca ?? throw new ArgumentNullException(nameof(tosca));
            _webDriver = webDriver;
            _sessionIdBuffer = sessionIdBuffer;
            _explicitSessionId = string.IsNullOrWhiteSpace(explicitSessionId)
                ? null
                : explicitSessionId.Trim();
            _fallbackSessionId = fallbackSessionId ?? "tosca-session";
        }

        /// <summary>
        /// The session id, or a placeholder that keeps the per-session caches working but is not a
        /// session anything can be asked about — see <see cref="HasRealSessionId"/>.
        ///
        /// An id given on the module is preferred: Tosca resolves a <c>{B[...]}</c> reference in a
        /// parameter value before handing it over, which beats reflecting into its buffer store.
        /// </summary>
        public string SessionId
        {
            get
            {
                if (_explicitSessionId != null) return _explicitSessionId;

                string? buffered = _tosca.Buffer(_sessionIdBuffer ?? DefaultSessionIdBuffer);
                return string.IsNullOrWhiteSpace(buffered) ? _fallbackSessionId : buffered.Trim();
            }
        }

        /// <summary>
        /// Whether a real session id was found. A placeholder would 404 against
        /// <c>/session/{id}/screenshot</c> and say nothing about the buffer being unset.
        /// </summary>
        public bool HasRealSessionId =>
            _explicitSessionId != null
            || !string.IsNullOrWhiteSpace(_tosca.Buffer(_sessionIdBuffer ?? DefaultSessionIdBuffer));

        public string? Host
        {
            get
            {
                string? server = _tosca.TestConfigurationParameter(AppiumServerTcp);
                return string.IsNullOrWhiteSpace(server) ? null : server.Trim();
            }
        }

        public string? PlatformName
        {
            get
            {
                return Capabilities.GetString("platformName");
            }
        }

        /// <summary>Built on first use: the best source is the session, which needs Host resolved.</summary>
        public IReadOnlyDictionary<string, object?> Capabilities => _capabilities ??= BuildCapabilities();

        /// <summary>Asked of the session when no parameter carries one: a test can rotate the device.</summary>
        public string? Orientation =>
            Capabilities.GetString("orientation") ?? Session()?.TryGetOrientation();

        /// <summary>
        /// Used only by iOS's scale factor. Zero degrades that factor to 1, which is correct whenever
        /// the real and logical widths match.
        /// </summary>
        public int WindowWidth => Session()?.TryGetWindowWidth() ?? 0;

        /// <summary>
        /// Tosca cannot pass a raw Appium command through, but the hub accepts one over the same HTTP
        /// route the screenshot uses — which is what makes App Automate's own capture reachable.
        /// </summary>
        public string? ExecuteScript(string script) => Session()?.ExecuteScript(script);

        public string GetScreenshotBase64()
        {
            string? fromSession = TryCaptureOverWebDriver();
            if (fromSession != null) return fromSession;

            throw new PercyException(
                "Could not capture the device screen. Check that the AppiumServer test configuration " +
                "parameter points at your BrowserStack hub, that the SessionId parameter carries the " +
                "session id (the 'Get Appium Session Id' module writes it to a buffer; pass it as " +
                "{B[PercyAppiumSessionId]}), and that the test is steering a device at this point.");
        }

        private string? TryCaptureOverWebDriver()
        {
            WebDriverSession? session = Session();
            if (session == null) return null;

            Utils.Log($"Capturing from the device session at {Utils.RedactCredentials(session.Endpoint)}.");
            return session.TryGetScreenshotBase64();
        }

        private WebDriverSession? _session;
        private bool _sessionResolved;

        /// <summary>
        /// The session client, once. Screenshots and scripts both go through it, so the reasons it is
        /// unavailable are reported here rather than at each call site.
        /// </summary>
        private WebDriverSession? Session()
        {
            if (_sessionResolved) return _session;
            _sessionResolved = true;

            if (_webDriver == null) return null;

            string? server = Host;
            if (string.IsNullOrWhiteSpace(server))
            {
                Utils.Log("No AppiumServer test configuration parameter, so the device session cannot " +
                    "be reached.", "debug");
                return null;
            }
            if (!HasRealSessionId)
            {
                Utils.Log("The Appium session id is not available, so the device session cannot be " +
                    "reached. The most direct fix: add the 'Get Appium Session Id' standard module " +
                    "before this step, then set the Percy module's SessionId parameter to " +
                    $"{{B[{_sessionIdBuffer ?? DefaultSessionIdBuffer}]}} so Tosca hands the id over.");
                return null;
            }

            return _session = _webDriver(server, SessionId);
        }

        /// <summary>
        /// Always null: the session cannot be queried for elements from here. Callers treat null as
        /// "region absent" and log it, rather than inventing coordinates.
        /// </summary>
        public ElementRect? FindElementByXPath(string xpath)
        {
            WarnRegionsUnavailable();
            return null;
        }

        public ElementRect? FindElementByAccessibilityId(string accessibilityId)
        {
            WarnRegionsUnavailable();
            return null;
        }

        private bool _warnedAboutRegions;

        private void WarnRegionsUnavailable()
        {
            // Once per driver: several locators would otherwise bury the rest of the log.
            if (_warnedAboutRegions) return;
            _warnedAboutRegions = true;
            Utils.Log("Element-based ignore and consider regions are not supported on Tosca — the " +
                "mobile engine cannot be queried for elements from an extension. Use " +
                "CustomIgnoreRegions / CustomConsiderRegions with pixel coordinates instead.", "warn");
        }

        /// <summary>
        /// Assembles the capability bag, weakest source first: test configuration parameters, then the
        /// session's own capabilities. The session wins because it describes the device that was
        /// allocated, where a parameter describes what was asked for.
        ///
        /// Every TCP is carried through under its own name as well as the mapped one, so a detail this
        /// SDK does not interpret is still visible.
        /// </summary>
        private Dictionary<string, object?> BuildCapabilities()
        {
            Dictionary<string, object?> capabilities = new Dictionary<string, object?>();

            IReadOnlyDictionary<string, string?> tcps = _tosca.TestConfigurationParameters();
            foreach (KeyValuePair<string, string?> tcp in tcps)
            {
                if (!string.IsNullOrWhiteSpace(tcp.Value)) capabilities[tcp.Key] = tcp.Value;
            }

            FillDeviceFactsFromSession(capabilities);

            foreach ((string capability, string[] names) in CapabilityMap)
            {
                // The session's answer is never overwritten by the parameter that requested it.
                if (capabilities.ContainsKey(capability)) continue;

                foreach (string name in names)
                {
                    string? value = Lookup(tcps, name);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        capabilities[capability] = value.Trim();
                        break;
                    }
                }
            }

            if (capabilities.Count == 0)
            {
                Utils.Log("No device details are available: Tosca reported no test configuration " +
                    "parameters and the session reported no capabilities. They are read from the " +
                    "device session, so this snapshot will be tagged with nothing identifying the " +
                    "device. Check that the SessionId parameter carries the Appium session id and " +
                    "that the AppiumServer test configuration parameter points at your hub.", "warn");
            }
            return capabilities;
        }

        /// <summary>
        /// BrowserStack reports the UDID as the top-level <c>deviceName</c> ("19171FDF6000AM") and the
        /// readable name under <c>desired</c>. Percy groups by the tag's device name, so a UDID would
        /// give every device a baseline named after a serial number.
        /// </summary>
        private static void PreferTheFriendlyDeviceName(
            Dictionary<string, object?> capabilities, IReadOnlyDictionary<string, object?> reported)
        {
            string? requested = Caps.AsDictionary(
                reported.TryGetValue("desired", out object? desired) ? desired : null)?.GetString("deviceName");

            if (string.IsNullOrWhiteSpace(requested)) return;

            string? current = capabilities.GetString("deviceName");
            if (string.Equals(current, requested, StringComparison.OrdinalIgnoreCase)) return;

            capabilities["deviceName"] = requested;
            Utils.Log($"Using '{requested}' as the device name rather than the session's " +
                $"'{current}', which is the device identifier.", "debug");
        }

        /// <summary>
        /// Asks the session for the device facts, under the same capability names the other App Percy
        /// SDKs read off their driver, so the metadata layer needs no Tosca-specific path.
        ///
        /// Several probes because no one endpoint answers all of it, and each is independent: one
        /// refusing costs its own detail only.
        /// </summary>
        private void FillDeviceFactsFromSession(Dictionary<string, object?> capabilities)
        {
            WebDriverSession? session = Session();
            if (session == null) return;

            IReadOnlyDictionary<string, object?>? reported = session.TryGetCapabilities();
            if (reported != null)
            {
                foreach (KeyValuePair<string, object?> capability in reported)
                {
                    if (capability.Value != null) capabilities[capability.Key] = capability.Value;
                }
                Utils.Log($"Read {reported.Count} capabilities from the device session.", "debug");
                PreferTheFriendlyDeviceName(capabilities, reported);
            }

            // "WxH" is what Android reports and what iOS falls back to for an unlisted device.
            (int Width, int Height)? window = session.TryGetWindowSize();
            if (window != null && !capabilities.ContainsKey("deviceScreenSize"))
            {
                capabilities["deviceScreenSize"] = $"{window.Value.Width}x{window.Value.Height}";
                Utils.Log($"Device screen is {window.Value.Width}x{window.Value.Height}, " +
                    "from the session window.", "debug");
            }

            if (capabilities.ContainsKey("viewportRect")) return;

            // Preferred: the usable area stated directly.
            IReadOnlyDictionary<string, object?>? viewport = session.TryGetViewportRect();
            if (viewport != null)
            {
                capabilities["viewportRect"] = viewport;
                Utils.Log("Read the viewport rect from the session.", "debug");
                return;
            }

            // Otherwise from the bar heights, the one route that reliably answers on Android.
            (int StatusBar, int NavigationBar)? bars = session.TryGetSystemBars();
            if (bars == null)
            {
                Utils.Log("The session reported neither a viewport rect nor system bars, so the " +
                    "status and navigation bars cannot be cropped and the clock in the status bar " +
                    "will differ between runs. The bar heights are read from the session; there is " +
                    "no parameter to supply them. Run with PERCY_LOGLEVEL=debug to see what the " +
                    "session refused.", "warn");
                return;
            }

            int? screenHeight = window?.Height
                ?? Caps.ToInt(capabilities.GetString("deviceScreenSize")?.Split('x').LastOrDefault());
            if (screenHeight == null)
            {
                // Worth keeping even alone: the metadata layer reads top from here.
                capabilities["viewportRect"] = new Dictionary<string, object?> { ["top"] = bars.Value.StatusBar };
                return;
            }

            capabilities["viewportRect"] = new Dictionary<string, object?>
            {
                ["top"] = bars.Value.StatusBar,
                ["left"] = 0,
                ["width"] = window?.Width,
                ["height"] = screenHeight.Value - bars.Value.StatusBar - bars.Value.NavigationBar
            };
            Utils.Log($"Status bar {bars.Value.StatusBar}px, navigation bar " +
                $"{bars.Value.NavigationBar}px, from the session's system bars.", "debug");
        }

        /// <summary>Case-insensitive: TCP names vary across connection types (OSVersion/OsVersion).</summary>
        private static string? Lookup(IReadOnlyDictionary<string, string?> tcps, string name)
        {
            if (tcps.TryGetValue(name, out string? exact)) return exact;

            foreach (KeyValuePair<string, string?> tcp in tcps)
            {
                if (string.Equals(tcp.Key, name, StringComparison.OrdinalIgnoreCase)) return tcp.Value;
            }
            return null;
        }
    }
}
