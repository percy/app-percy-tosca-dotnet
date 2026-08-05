using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    /// <summary>
    /// In-memory stand-in for what Tosca provides: test configuration parameters, buffers, and the
    /// mobile engine's screenshot task.
    /// </summary>
    public class StubToscaEnvironment : IToscaEnvironment
    {
        public Dictionary<string, string?> Tcps { get; } = new Dictionary<string, string?>();
        public Dictionary<string, string?> Buffers { get; } = new Dictionary<string, string?>();


        public string? TestConfigurationParameter(string name) =>
            Tcps.TryGetValue(name, out string? value) ? value : null;

        public IReadOnlyDictionary<string, string?> TestConfigurationParameters() => Tcps;

        public string? Buffer(string name) =>
            Buffers.TryGetValue(name, out string? value) ? value : null;


        /// <summary>An App Automate mobile session with the TCPs Tosca sets for one.</summary>
        public static StubToscaEnvironment AppAutomate()
        {
            StubToscaEnvironment tosca = new StubToscaEnvironment();
            tosca.Tcps["AppiumServer"] = "https://hub-cloud.browserstack.com/wd/hub";
            tosca.Tcps["DeviceName"] = "Google Pixel 7";
            tosca.Tcps["MobileOS"] = "Android";
            tosca.Tcps["OSVersion"] = "13.0";
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = "session-abc";
            return tosca;
        }
    }

    public class ToscaMobileDriverTests : CoreTestBase
    {
        private static ToscaMobileDriver Build(
            StubToscaEnvironment tosca, ScreenshotOptions? options = null, string? buffer = null,
            StubHttpMessageHandler? deviceHttp = null,
            Func<string?, AutomateSessionFinder.Hints, string?>? discover = null) =>
            new ToscaMobileDriver(tosca, options ?? new ScreenshotOptions(), buffer, null,
                deviceHttp == null
                    ? null
                    : (server, sessionId) =>
                        new WebDriverSession(deviceHttp.Client(), server, sessionId),
                discover);

        /// <summary>A device session that answers the WebDriver screenshot endpoint.</summary>
        private static StubHttpMessageHandler DeviceServing(string base64) =>
            new StubHttpMessageHandler().Default("{\"value\":\"" + base64 + "\"}");

        [Fact]
        public void TheHubUrlComesFromTheAppiumServerParameter()
        {
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());

            Assert.Equal("https://hub-cloud.browserstack.com/wd/hub", driver.Host);
        }

        [Fact]
        public void AnAppAutomateSessionReachableOverHttpCanExecuteScripts()
        {
            // The correction that made the App Automate provider usable at all: Tosca will not pass a
            // raw Appium command through, but the hub accepts one directly over HTTP. Scripting was
            // never unavailable — only unavailable through Tosca — and it is what full page needs.
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate(),
                deviceHttp: DeviceServing(StubMobileDriver.ValidPngBase64));

            Assert.True(AppAutomate.Supports(driver));
            Assert.True(driver.CanExecuteScript);
        }

        [Fact]
        public void WithoutAReachableSessionScriptingIsUnavailable()
        {
            // No session client, so nothing to send a script through.
            Assert.False(Build(StubToscaEnvironment.AppAutomate()).CanExecuteScript);
        }

        [Fact]
        public void AScriptIsSentToTheSessionsExecuteEndpoint()
        {
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default("{\"value\":\"{\\\"success\\\":true}\"}");
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate(), deviceHttp: device);

            Assert.Equal("{\"success\":true}",
                driver.ExecuteScript("browserstack_executor: {}"));
            Assert.Contains("/session/session-abc/execute/sync", device.Requests[0].Url);
            Assert.Contains("browserstack_executor", device.Requests[0].Body!);
        }



        [Fact]
        public void TheSessionIdComesFromItsBuffer()
        {
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());

            Assert.Equal("session-abc", driver.SessionId);
            Assert.True(driver.HasRealSessionId);
        }

        [Fact]
        public void TheSessionIdBufferNameIsOverridable()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers["MyOwnBuffer"] = "session-xyz";

            Assert.Equal("session-xyz", Build(tosca, buffer: "MyOwnBuffer").SessionId);
        }

        [Fact]
        public void TheSessionIdIsTrimmed()
        {
            // A buffer written by a Tosca module commonly carries surrounding whitespace, and the
            // CLI would fail to attach to a session id with a newline in it.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = "  session-abc\n";

            Assert.Equal("session-abc", Build(tosca).SessionId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AMissingSessionIdIsReportedAsSuchRatherThanSubstitutedSilently(string? buffered)
        {
            // Capture asks /session/{id}/screenshot, so a placeholder would 404 with an error that
            // never mentions the buffer actually being unset.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = buffered;
            ToscaMobileDriver driver = Build(tosca);

            Assert.False(driver.HasRealSessionId);
            // Still stable, because the per-session caches key on it.
            Assert.False(string.IsNullOrWhiteSpace(driver.SessionId));
            Assert.Equal(driver.SessionId, driver.SessionId);
        }

        [Fact]
        public void TheSessionIdCanBeDiscoveredWhenNoBufferHoldsIt()
        {
            // The case that matters on App Automate, where the 'Get Appium Session Id' module is not
            // always usable and there is no mobile screenshot task to fall back to either.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = null;

            ToscaMobileDriver driver = Build(tosca, discover: (_, _) => "discovered-1");

            Assert.Equal("discovered-1", driver.SessionId);
            Assert.True(driver.HasRealSessionId);
        }

        [Fact]
        public void ABufferedSessionIdIsPreferredOverADiscoveredOne()
        {
            // The buffer came from the session actually under test; discovery infers from "what is
            // running on this account", which is only right when one thing is.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            bool asked = false;

            ToscaMobileDriver driver = Build(tosca, discover: (_, _) => { asked = true; return "discovered"; });

            Assert.Equal("session-abc", driver.SessionId);
            Assert.False(asked);
        }

        [Fact]
        public void DiscoveryIsAttemptedOncePerDriverEvenWhenItFails()
        {
            // Otherwise every property read costs a BrowserStack API call.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = null;
            int calls = 0;

            ToscaMobileDriver driver = Build(tosca, discover: (_, _) => { calls++; return null; });

            Assert.False(driver.HasRealSessionId);
            _ = driver.SessionId;
            _ = driver.SessionId;
            Assert.Equal(1, calls);
        }

        [Fact]
        public void DiscoveryIsHandedTheDeviceDetailsThatDistinguishSessions()
        {
            // Without these, five running sessions on a shared account are indistinguishable.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = null;
            AutomateSessionFinder.Hints? seen = null;

            _ = Build(tosca, discover: (_, hints) => { seen = hints; return null; }).SessionId;

            Assert.Equal("Google Pixel 7", seen!.DeviceName);
            Assert.Equal("13.0", seen.OsVersion);
        }

        [Fact]
        public void DiscoveryIsHandedTheHubUrlToAuthenticateWith()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = null;
            string? seen = null;

            _ = Build(tosca, discover: (hub, _) => { seen = hub; return null; }).SessionId;

            Assert.Equal("https://hub-cloud.browserstack.com/wd/hub", seen);
        }

        [Fact]
        public void AFallbackSessionKeyCanBeSupplied()
        {
            StubToscaEnvironment tosca = new StubToscaEnvironment();

            Assert.Equal("step-7",
                new ToscaMobileDriver(tosca, new ScreenshotOptions(), null, "step-7").SessionId);
        }

        [Fact]
        public void DeviceMetadataIsMappedFromTheTestConfigurationParameters()
        {
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());

            Assert.Equal("Google Pixel 7", driver.Capabilities.GetString("deviceName"));
            Assert.Equal("Android", driver.Capabilities.GetString("platformName"));
            Assert.Equal("13.0", driver.Capabilities.GetString("platformVersion"));
            Assert.Equal("Android", driver.PlatformName);
        }

        [Fact]
        public void EveryParameterIsCarriedThroughUnderItsOwnNameAsWell()
        {
            // The whole parameter set is carried through as capabilities, so a detail this SDK does
            // not map by name is still available to anything that reads them.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Tcps["browserstack.user"] = "someone";

            ToscaMobileDriver driver = Build(tosca);

            Assert.Equal("someone", driver.Capabilities.GetString("browserstack.user"));
            Assert.Equal("https://hub-cloud.browserstack.com/wd/hub",
                driver.Capabilities.GetString("AppiumServer"));
        }

        [Fact]
        public void ParameterNamesAreMatchedCaseInsensitively()
        {
            // The engine's parameter set spells these differently between connection types.
            StubToscaEnvironment tosca = new StubToscaEnvironment();
            tosca.Tcps["osversion"] = "16.4";
            tosca.Tcps["devicename"] = "iPhone X";

            ToscaMobileDriver driver = Build(tosca);

            Assert.Equal("16.4", driver.Capabilities.GetString("platformVersion"));
            Assert.Equal("iPhone X", driver.Capabilities.GetString("deviceName"));
        }

        [Fact]
        public void TheAlternativeParameterSpellingsAreAllRead()
        {
            StubToscaEnvironment tosca = new StubToscaEnvironment();
            tosca.Tcps["Device"] = "Galaxy S23";
            tosca.Tcps["Platform"] = "Android";
            tosca.Tcps["MobileOSVersion"] = "14";
            tosca.Tcps["ScreenResolution"] = "1080x2340";
            tosca.Tcps["DeviceOrientation"] = "landscape";
            tosca.Tcps["App"] = "bs://abc";
            tosca.Tcps["UDID"] = "udid-1";

            ToscaMobileDriver driver = Build(tosca);

            Assert.Equal("Galaxy S23", driver.Capabilities.GetString("deviceName"));
            Assert.Equal("Android", driver.Capabilities.GetString("platformName"));
            Assert.Equal("14", driver.Capabilities.GetString("platformVersion"));
            Assert.Equal("1080x2340", driver.Capabilities.GetString("deviceScreenSize"));
            Assert.Equal("landscape", driver.Orientation);
            Assert.Equal("bs://abc", driver.Capabilities.GetString("app"));
            Assert.Equal("udid-1", driver.Capabilities.GetString("udid"));
        }

        [Fact]
        public void BlankParametersAreTreatedAsUnset()
        {
            StubToscaEnvironment tosca = new StubToscaEnvironment();
            tosca.Tcps["DeviceName"] = "   ";
            tosca.Tcps["Device"] = "Galaxy S23";

            Assert.Equal("Galaxy S23", Build(tosca).Capabilities.GetString("deviceName"));
        }

        [Fact]
        public void ModuleParametersOverrideTheTestConfiguration()
        {
            // The documented way to supply what the parameters do not carry, so they have to win.
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate(), new ScreenshotOptions
            {
                DeviceName = "iPhone X",
                OsName = "iOS",
                PlatformVersion = "16.4"
            });

            Assert.Equal("iPhone X", driver.Capabilities.GetString("deviceName"));
            Assert.Equal("iOS", driver.Capabilities.GetString("platformName"));
            Assert.Equal("16.4", driver.Capabilities.GetString("platformVersion"));
            Assert.Equal("iOS", driver.PlatformName);
        }

        [Fact]
        public void ASessionWithNoParametersAtAllSaysWhatToSetInstead()
        {
            ToscaMobileDriver driver = Build(new StubToscaEnvironment());

            Assert.Empty(driver.Capabilities);
            Assert.Null(driver.Host);
            Assert.Null(driver.PlatformName);
            Assert.True(Logged("Set DeviceName, OsName and OsVersion"));
        }

        [Fact]
        public void ANullEnvironmentOrOptionsIsRefusedAtConstruction()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ToscaMobileDriver(null!, new ScreenshotOptions()));
            Assert.Throws<ArgumentNullException>(() =>
                new ToscaMobileDriver(new StubToscaEnvironment(), null!));
        }

        [Theory]
        [InlineData("percy.enabled", false, true)]
        [InlineData("percy.ignoreErrors", true, false)]
        public void PercyCanBeTurnedOffOrMadeStrictFromATestConfigurationParameter(
            string parameter, bool expectedEnabled, bool expectedIgnoreErrors)
        {
            // The other SDKs use a nested `percyOptions` capability for this. That shape cannot come
            // from a test configuration parameter, so on Tosca the flat `percy.*` spellings are the
            // route — and they work precisely because every parameter is carried into the capability
            // bag under its own name.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Tcps[parameter] = "false";
            ToscaMobileDriver driver = Build(tosca);
            PercyOptions options = new PercyOptions(driver, new Cache<string, object?>());

            Assert.Equal(expectedEnabled, options.PercyEnabled());
            Assert.Equal(expectedIgnoreErrors, options.IgnoreErrors());
        }

        [Fact]
        public void WithNoPercyParametersPercyIsEnabledAndForgiving()
        {
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());
            PercyOptions options = new PercyOptions(driver, new Cache<string, object?>());

            Assert.True(options.PercyEnabled());
            Assert.True(options.IgnoreErrors());
        }

        [Fact]
        public void TheWindowWidthIsUnavailableAndReportsZero()
        {
            // Only iOS's scale factor reads it, and that degrades to 1 — correct whenever the real
            // and logical widths match.
            Assert.Equal(0, Build(StubToscaEnvironment.AppAutomate()).WindowWidth);
        }

        [Fact]
        public void TheScreenIsCapturedFromTheDeviceSessionWhenItCanBeReached()
        {
            // The only capture route. Tosca's own screenshot task used to be a fallback here; it
            // depended on a task name that differs between releases and bought nothing once the
            // session turned out to be reachable directly.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = DeviceServing(StubMobileDriver.ValidPngBase64);

            Assert.Equal(StubMobileDriver.ValidPngBase64,
                Build(tosca, deviceHttp: device).GetScreenshotBase64());

            Assert.Contains("/session/session-abc/screenshot", device.Requests[0].Url);
        }


        [Fact]
        public void WithNoRouteToTheDeviceCaptureFailsWithSomethingActionable()
        {
            StubToscaEnvironment tosca = new StubToscaEnvironment();

            PercyException error = Assert.Throws<PercyException>(
                () => Build(tosca).GetScreenshotBase64());

            Assert.Contains("AppiumServer", error.Message);
            Assert.Contains("Get Appium Session Id", error.Message);
        }

        [Fact]
        public void WithoutASessionIdTheDeviceIsNotAskedAndTheModuleIsNamed()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = null;
            StubHttpMessageHandler device = DeviceServing(StubMobileDriver.ValidPngBase64);

            Assert.Throws<PercyException>(() => Build(tosca, deviceHttp: device).GetScreenshotBase64());

            Assert.Empty(device.Requests);
            Assert.True(Logged("Get Appium Session Id"));
        }

        [Fact]
        public void WithoutAServerUrlTheDeviceIsNotAsked()
        {
            SetEnv("PERCY_LOGLEVEL", "debug");
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Tcps.Remove("AppiumServer");
            StubHttpMessageHandler device = DeviceServing(StubMobileDriver.ValidPngBase64);

            Assert.Throws<PercyException>(() => Build(tosca, deviceHttp: device).GetScreenshotBase64());

            Assert.Empty(device.Requests);
            Assert.True(Logged("No AppiumServer"));
        }






        [Fact]
        public void ElementRegionsAreUnavailableAndSayWhatToUseInstead()
        {
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());

            Assert.Null(driver.FindElementByXPath("//total"));
            Assert.Null(driver.FindElementByAccessibilityId("banner"));

            Assert.True(Logged("not supported on Tosca"));
            Assert.True(Logged("CustomIgnoreRegions"));
        }

        [Fact]
        public void TheUnavailableRegionWarningIsGivenOnlyOnce()
        {
            // A module declaring several locators would otherwise repeat it per locator and bury
            // everything else in the log.
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());

            driver.FindElementByXPath("//a");
            driver.FindElementByXPath("//b");
            driver.FindElementByAccessibilityId("c");

            Assert.Single(Logs.Where(entry => entry.Message.Contains("not supported on Tosca")));
        }


    }
}
