namespace AppPercyTosca.Core
{
    /// <summary>
    /// A device session assembled from what Tosca actually exposes: test configuration parameters,
    /// buffers, and the mobile engine's screenshot task. See <see cref="IToscaEnvironment"/> for why
    /// there is nothing better to build on.
    ///
    /// The consequence worth understanding before reading on: this session cannot send raw Appium
    /// commands and cannot query elements. So the element-based ignore/consider regions and App
    /// Automate's remote full-page capture are unavailable here, while Percy on Automate — which
    /// needs only a session id, a hub URL and capabilities, and does all its work server-side — is
    /// fully supported. That asymmetry is why the README points Tosca users at Percy on Automate.
    /// </summary>
    public class ToscaMobileDriver : IMobileDriver
    {
        /// <summary>
        /// TCP holding the Appium endpoint the mobile engine connects to. For App Automate this is
        /// https://hub-cloud.browserstack.com/wd/hub.
        /// </summary>
        public const string AppiumServerTcp = "AppiumServer";

        /// <summary>
        /// Default buffer the Appium session id is read from. Percy on Automate needs it, and the
        /// only way to obtain it is the `Get Appium Session Id` standard module, which writes it to
        /// a buffer the user names — so the name is overridable from the Percy module.
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
        private readonly ScreenshotOptions _options;
        private readonly Func<string, string, WebDriverSession>? _webDriver;
        private readonly string? _sessionIdBuffer;
        private readonly Dictionary<string, object?> _capabilities;
        private readonly string _fallbackSessionId;

        /// <summary>
        /// Builds the session view for one snapshot. <paramref name="options"/> is consulted because
        /// on Tosca the module parameters are frequently the *only* source of device metadata — when
        /// the TCPs do not carry it, there is nowhere else to look.
        /// </summary>
        /// <param name="webDriver">
        /// Builds a session client for a (serverUrl, sessionId) pair. Supplied by the shim, which owns
        /// the HttpClient; null disables the WebDriver capture route and leaves only Tosca's own task.
        /// </param>
        public ToscaMobileDriver(
            IToscaEnvironment tosca,
            ScreenshotOptions options,
            string? sessionIdBuffer = null,
            string? fallbackSessionId = null,
            Func<string, string, WebDriverSession>? webDriver = null)
        {
            _tosca = tosca ?? throw new ArgumentNullException(nameof(tosca));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _webDriver = webDriver;
            _sessionIdBuffer = sessionIdBuffer;
            _fallbackSessionId = fallbackSessionId ?? "tosca-session";
            _capabilities = BuildCapabilities();
        }

        /// <summary>
        /// The Appium session id from its buffer, or a stable placeholder. The placeholder keeps the
        /// per-session caches working; it is not good enough for Percy on Automate, which is why
        /// <see cref="HasRealSessionId"/> exists for callers to check before taking that path.
        /// </summary>
        public string SessionId
        {
            get
            {
                string? buffered = _tosca.Buffer(_sessionIdBuffer ?? DefaultSessionIdBuffer);
                return string.IsNullOrWhiteSpace(buffered) ? _fallbackSessionId : buffered.Trim();
            }
        }

        /// <summary>
        /// Whether a real Appium session id was found. Percy on Automate posts the id to the CLI so
        /// it can reconnect to the session itself; sending a placeholder would have the CLI fail to
        /// attach, with an error that says nothing about the missing buffer.
        /// </summary>
        public bool HasRealSessionId =>
            !string.IsNullOrWhiteSpace(_tosca.Buffer(_sessionIdBuffer ?? DefaultSessionIdBuffer));

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
                if (!string.IsNullOrWhiteSpace(_options.OsName)) return _options.OsName;
                return _capabilities.GetString("platformName");
            }
        }

        public IReadOnlyDictionary<string, object?> Capabilities => _capabilities;

        public string? Orientation => _capabilities.GetString("orientation");

        /// <summary>
        /// Not obtainable: it would need a live driver to ask. Only iOS's scale factor uses it, and
        /// that degrades to 1 — which is correct whenever the device's real and logical widths match.
        /// </summary>
        public int WindowWidth => 0;

        /// <summary>
        /// False on Tosca. <c>Execute Driver Script</c> — the module that would carry a
        /// <c>browserstack_executor</c> command — is documented as available only against Tricentis'
        /// own device cloud, so it cannot be relied on against an App Automate hub.
        /// </summary>
        public bool CanExecuteScript => _tosca.CanExecuteScript;

        public string? ExecuteScript(string script) =>
            _tosca.CanExecuteScript ? _tosca.ExecuteScript(script) : null;

        /// <summary>
        /// Captures the screen through the mobile engine and returns it base64-encoded.
        ///
        /// This reads the PNG back off disk and re-encodes it, only for the caller to decode and
        /// write it out again. That round trip is deliberate: it keeps the capture path identical to
        /// every other App Percy SDK, which all speak base64, rather than introducing a second
        /// file-handling path through the providers for Tosca alone.
        /// </summary>
        public string GetScreenshotBase64()
        {
            // Preferred when both facts are available, because it depends only on the WebDriver
            // standard rather than on a Tosca task name that changes between releases.
            string? fromSession = TryCaptureOverWebDriver();
            if (fromSession != null) return fromSession;

            string? path = _tosca.CaptureScreenshot();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new PercyException(
                    "The Tosca mobile engine did not produce a screenshot. Check that the test is " +
                    "steering a mobile device, that the Mobile Engine 3.0 server is running, and that " +
                    "either the Appium session id is available in a buffer (the 'Get Appium Session " +
                    "Id' module) so the device can be captured directly, or the Percy module has " +
                    "Directory and Filename parameters for Tosca's own screenshot task to write to.");
            }
            if (!File.Exists(path))
            {
                throw new PercyException(
                    $"The Tosca mobile engine reported a screenshot at {path} but no file is there.");
            }

            try
            {
                return Convert.ToBase64String(File.ReadAllBytes(path));
            }
            finally
            {
                TryDelete(path);
            }
        }

        /// <summary>
        /// Asks the automation server for the screen directly. Needs the server URL and the real
        /// session id; without either there is nothing to ask, and null sends the caller to Tosca's
        /// own screenshot task instead.
        /// </summary>
        private string? TryCaptureOverWebDriver()
        {
            if (_webDriver == null) return null;

            string? server = Host;
            if (string.IsNullOrWhiteSpace(server))
            {
                Utils.Log("No AppiumServer test configuration parameter, so the device session cannot " +
                    "be asked for a screenshot directly.", "debug");
                return null;
            }
            if (!HasRealSessionId)
            {
                Utils.Log("The Appium session id is not available, so the device session cannot be " +
                    "asked for a screenshot directly. Add the 'Get Appium Session Id' standard module " +
                    $"before this step, writing to buffer '{_sessionIdBuffer ?? DefaultSessionIdBuffer}'.");
                return null;
            }

            WebDriverSession session = _webDriver(server, SessionId);
            Utils.Log($"Capturing from the device session at {Utils.RedactCredentials(session.Endpoint)}.");
            return session.TryGetScreenshotBase64();
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
        /// Assembles capabilities from the TCPs, then lets the module parameters override.
        ///
        /// Every TCP is carried through under its own name as well as the mapped one, because Percy
        /// on Automate forwards this dictionary to the CLI, which recognises more of the connection
        /// details than this SDK does — dropping the unmapped ones would lose information the CLI
        /// could have used.
        /// </summary>
        private Dictionary<string, object?> BuildCapabilities()
        {
            Dictionary<string, object?> capabilities = new Dictionary<string, object?>();

            IReadOnlyDictionary<string, string?> tcps = _tosca.TestConfigurationParameters();
            foreach (KeyValuePair<string, string?> tcp in tcps)
            {
                if (!string.IsNullOrWhiteSpace(tcp.Value)) capabilities[tcp.Key] = tcp.Value;
            }

            foreach ((string capability, string[] names) in CapabilityMap)
            {
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

            // Module parameters last: they are the documented way to supply what the TCPs do not
            // carry, so they have to win.
            if (!string.IsNullOrWhiteSpace(_options.DeviceName))
                capabilities["deviceName"] = _options.DeviceName;
            if (!string.IsNullOrWhiteSpace(_options.OsName))
                capabilities["platformName"] = _options.OsName;
            if (!string.IsNullOrWhiteSpace(_options.PlatformVersion))
                capabilities["platformVersion"] = _options.PlatformVersion;

            if (capabilities.Count == 0)
            {
                Utils.Log("No test configuration parameters were found, so no device details are " +
                    "available from Tosca. Set DeviceName, OsName and OsVersion on the Percy " +
                    "module.", "warn");
            }
            return capabilities;
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

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e)
            {
                // The tile the CLI reads is written separately; this is only the engine's scratch
                // copy, so a locked file costs a stray temp file and nothing else.
                Utils.Log($"Could not delete the temporary screenshot {path}: {e.Message}", "debug");
            }
        }
    }
}
