// The IMobileDriver.Capabilities property shadows the static Capabilities helper class, so the
// helpers are reached through this alias rather than by name.
using Caps = AppPercyTosca.Core.Capabilities;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// A device session assembled from the two things Tosca exposes: the hub address, from the
    /// AppiumServer test configuration parameter, and the Appium session id. See
    /// <see cref="IToscaEnvironment"/> for why there is nothing better to build on.
    ///
    /// With those two, everything else is plain WebDriver over HTTP and needs no Tricentis API at
    /// all — screenshots, capabilities, orientation, system bars, and the browserstack_executor
    /// scripts that make full-page capture work.
    ///
    /// The one thing still out of reach is querying for elements, so element-based ignore and consider
    /// regions are unavailable and regions have to be given in pixels.
    /// </summary>
    public class ToscaMobileDriver : IMobileDriver
    {
        /// <summary>
        /// TCP holding the Appium endpoint the mobile engine connects to. For App Automate this is
        /// https://hub-cloud.browserstack.com/wd/hub.
        /// </summary>
        public const string AppiumServerTcp = "AppiumServer";

        /// <summary>
        /// Default buffer the Appium session id is read from. Capture needs it to ask the device
        /// session for the screen, and the only way to obtain it is the `Get Appium Session Id`
        /// standard module, which writes it to a buffer the user names — so the name is overridable
        /// from the Percy module.
        /// </summary>
        public const string DefaultSessionIdBuffer = "PercyAppiumSessionId";

        /// <summary>
        /// TCP names read as device metadata, mapped to the capability names the metadata layer and
        /// the CLI already understand. Several spellings per capability because the engine's TCP set
        /// differs between connection types (local device vs. cloud).
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

        /// <summary>
        /// Builds the session view for one snapshot.
        ///
        /// This used to take the step's <c>ScreenshotOptions</c> as well, from when the module carried
        /// DeviceName, OsName and the screen size and those were often the only source of device
        /// metadata. They are read from the session now, so the options said nothing this needed.
        /// </summary>
        /// <param name="webDriver">
        /// Builds a session client for a (serverUrl, sessionId) pair. Supplied by the shim, which owns
        /// the HttpClient; null leaves the device unreachable and every fact unanswered.
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
        /// The session id, from the best source available — or a stable placeholder, which keeps the
        /// per-session caches working but is not a session anything can be asked about. That is what
        /// <see cref="HasRealSessionId"/> is for.
        ///
        /// Both sources are exact. An id given on the module is best: Tosca resolves a
        /// <c>{B[...]}</c> buffer reference in a parameter value before handing it over, so it is the
        /// same id the `Get Appium Session Id` module captured, delivered through documented Tosca
        /// behaviour rather than by reflecting into Tosca's buffer store.
        ///
        /// There used to be a third source — asking BrowserStack which session was running — and it is
        /// gone on purpose. It inferred rather than knew, and on a shared account it could capture the
        /// wrong device and produce a snapshot plausible enough to be accepted as a baseline. Narrowing
        /// by device made that less likely without making it safe.
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
        /// Whether a real Appium session id was found. Capture asks the automation server for
        /// <c>/session/{id}/screenshot</c>, so a placeholder would produce a 404 that says nothing
        /// about the buffer actually being unset.
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

        /// <summary>
        /// Built on first use rather than in the constructor, because the best source is the session
        /// itself and reaching that needs the host and session id to be resolved first.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Capabilities => _capabilities ??= BuildCapabilities();

        /// <summary>
        /// The device's current orientation. Asked of the session when no parameter carries one, since a
        /// test can rotate the device and Tosca reports nothing about it.
        /// </summary>
        public string? Orientation =>
            Capabilities.GetString("orientation") ?? Session()?.TryGetOrientation();

        /// <summary>
        /// The session window's width, used only by iOS's scale factor. Zero when the session will not
        /// report it, which degrades that factor to 1 — correct whenever the real and logical widths
        /// match.
        /// </summary>
        public int WindowWidth => Session()?.TryGetWindowWidth() ?? 0;

        /// <summary>
        /// True whenever the session can be reached over HTTP.
        ///
        /// This used to be false, on the reasoning that Tosca's <c>Execute Driver Script</c> module is
        /// restricted to Tricentis' own device cloud. That conflated two things: Tosca cannot pass a raw
        /// Appium command through, but the hub accepts one directly over the same route the screenshot
        /// uses. Scripting was never unavailable — only unavailable *through Tosca* — and it is what
        /// makes App Automate's own capture, and therefore full page, reachable.
        /// </summary>
        public bool CanExecuteScript => Session() != null;

        public string? ExecuteScript(string script) => Session()?.ExecuteScript(script);

        /// <summary>
        /// Captures the screen.
        ///
        /// This reads the PNG back off disk and re-encodes it, only for the caller to decode and
        /// write it out again. That round trip is deliberate: it keeps the capture path identical to
        /// every other App Percy SDK, which all speak base64, rather than introducing a second
        /// file-handling path through the providers for Tosca alone.
        /// </summary>
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

        /// <summary>
        /// Asks the automation server for the screen directly. Needs the server URL and the real
        /// session id; without either there is nothing to ask, and null sends the caller to Tosca's
        /// own screenshot task instead.
        /// </summary>
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
        /// The session client, once, or null when the two facts it needs are not both available. Both
        /// screenshots and scripts go through it, so the reasons it is unavailable are reported here
        /// rather than duplicated at each call site.
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
        /// Always null: a Tosca mobile session cannot be queried for elements from here, so
        /// element-based ignore and consider regions are not available. Callers treat a null as
        /// "region absent" and log it, which is the honest outcome — the alternative would be
        /// coordinates invented from nothing.
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
            // Once per driver: a module declaring several locators would otherwise repeat this for
            // each one and bury everything else in the log.
            if (_warnedAboutRegions) return;
            _warnedAboutRegions = true;
            Utils.Log("Element-based ignore and consider regions are not supported on Tosca — the " +
                "mobile engine cannot be queried for elements from an extension. Use " +
                "CustomIgnoreRegions / CustomConsiderRegions with pixel coordinates instead.", "warn");
        }

        /// <summary>
        /// Assembles the capability bag from three sources, weakest first: the test configuration
        /// parameters, then the session's own capabilities, then the module parameters.
        ///
        /// The session outranks the test configuration on purpose. Tosca sets no parameters for screen
        /// size, orientation or OS version, so on their own the comparison tag went out empty — and
        /// where both do have an opinion, the session describes the device that was actually allocated
        /// while a parameter describes what was asked for. Module parameters win over both, being an
        /// explicit override.
        ///
        /// Every TCP is also carried through under its own name, not just the mapped one, so a detail
        /// this SDK does not interpret is still visible to anything that reads capabilities.
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
                // Skipped when the session already reported this capability, so mapping a parameter
                // onto it cannot overwrite the device's own answer with the requested one.
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
        /// Replaces the session's <c>deviceName</c> with the human one where they differ.
        ///
        /// BrowserStack reports the UDID as the top-level <c>deviceName</c> — "19171FDF6000AM" — and puts
        /// the readable name in <c>device</c> and in the requested capabilities under <c>desired</c>.
        /// Percy groups comparisons by the device name in the tag, so a UDID would give every device its
        /// own baseline named after a serial number. Android reads <c>device</c> first and escapes this;
        /// iOS reads <c>deviceName</c> and would not.
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
        /// Fills in the device facts the tag needs by asking the session, under the same capability
        /// names the other App Percy SDKs read off their driver — so the metadata layer needs no
        /// Tosca-specific path and derives the bars exactly as it does everywhere else.
        ///
        /// Four probes rather than one because there is no single endpoint that answers all of it.
        /// GET /session/{id} would, but W3C dropped it, so it is tried first and not relied on; the rest
        /// are the documented per-fact routes. Each is independent: one refusing costs its own detail
        /// only, and says so.
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

            // The full screen size. Android reports it as "WxH"; this is also what iOS falls back to
            // when the device is absent from the built-in table.
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

            // Otherwise build it from the bar heights, which is the one route that reliably answers on
            // Android. Without it nothing is cropped and the status bar clock differs on every run.
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
                // Bar heights alone are still worth having; the metadata layer reads top from here.
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

        /// <summary>
        /// TCP names are case-inconsistent across connection types (OSVersion vs. OsVersion), so
        /// lookup is case-insensitive rather than exact.
        /// </summary>
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
