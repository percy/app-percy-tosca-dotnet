using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    /// <summary>In-memory stand-in for what Tosca provides: parameters and buffers.</summary>
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
            string? sessionId = null) =>
            new ToscaMobileDriver(tosca, buffer, null, sessionId,
                deviceHttp == null
                    ? null
                    : (server, sessionId) =>
                        new WebDriverSession(deviceHttp.Client(), server, sessionId));

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
            // What makes the App Automate provider usable at all: Tosca will not pass a raw Appium
            // command through, but the hub accepts one directly over HTTP.
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate(),
                deviceHttp: new StubHttpMessageHandler().Default("{\"value\":\"ran\"}"));

            Assert.True(AppAutomate.Supports(driver));
            Assert.Equal("ran", driver.ExecuteScript("browserstack_executor: {}"));
        }

        [Fact]
        public void WithoutAReachableSessionScriptingIsUnavailable()
        {
            // No session client, so nothing to send a script through.
            Assert.Null(Build(StubToscaEnvironment.AppAutomate()).ExecuteScript("anything"));
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
        public void AnIdGivenOnTheModuleWinsOverEverythingElse()
        {
            // Tosca resolves a {B[...]} buffer reference in a parameter value before handing it over, so
            // this is the id the 'Get Appium Session Id' module captured — arriving through documented
            // Tosca behaviour rather than by reflecting into Tosca's buffer store.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();

            ToscaMobileDriver driver = Build(tosca, sessionId: "from-parameter");

            Assert.Equal("from-parameter", driver.SessionId);
            Assert.True(driver.HasRealSessionId);
        }

        [Fact]
        public void AnIdGivenOnTheModuleIsTrimmed()
        {
            // A resolved buffer reference commonly arrives with surrounding whitespace, and the CLI
            // would fail to attach to an id with a newline in it.
            Assert.Equal("s-1",
                Build(StubToscaEnvironment.AppAutomate(), sessionId: "  s-1\n").SessionId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ABlankSessionIdParameterIsTreatedAsUnset(string given)
        {
            // An empty row must not shadow the buffer that does have the id.
            Assert.Equal("session-abc",
                Build(StubToscaEnvironment.AppAutomate(), sessionId: given).SessionId);
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
        public void AFallbackSessionKeyCanBeSupplied()
        {
            StubToscaEnvironment tosca = new StubToscaEnvironment();

            Assert.Equal("step-7",
                new ToscaMobileDriver(tosca, null, "step-7").SessionId);
        }

        [Fact]
        public void DeviceDetailsAreReadFromTheSessionWhenToscaSetsNoParameters()
        {
            // The whole reason for asking the session: Tosca sets nothing for screen size, orientation
            // or OS version, so the comparison tag went out empty. The hub knows all of it.
            StubToscaEnvironment tosca = new StubToscaEnvironment();
            tosca.Tcps["AppiumServer"] = "https://hub-cloud.browserstack.com/wd/hub";
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = "s-1";
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default("{\"value\":{\"platformName\":\"Android\",\"platformVersion\":\"13\"," +
                    "\"deviceName\":\"Google Pixel 7\",\"deviceScreenSize\":\"1080x2400\"}}");

            ToscaMobileDriver driver = Build(tosca, deviceHttp: device);

            Assert.Equal("Android", driver.PlatformName);
            Assert.Equal("13", driver.Capabilities.GetString("platformVersion"));
            Assert.Equal("Google Pixel 7", driver.Capabilities.GetString("deviceName"));
            Assert.Equal("1080x2400", driver.Capabilities.GetString("deviceScreenSize"));
            Assert.Contains("/session/s-1", device.Requests[0].Url);
        }

        /// <summary>
        /// A real BrowserStack App Automate reply, credentials removed. Kept verbatim because the shape
        /// is the thing under test: an earlier version required the map to arrive under a particular
        /// envelope and silently produced an empty tag against exactly this payload.
        /// </summary>
        private const string RealSessionCapabilities =
            "{\"value\":{\"platform\":\"LINUX\",\"webStorageEnabled\":false,\"takesScreenshot\":true,\"javascriptEnabled\":true,\"networkConnectionEnabled\":true,\"warnings\":{},\"desired\":{\"platformName\":\"Android\",\"deviceName\":\"Google Pixel 6\",\"automationName\":\"UIAutomator2\",\"udid\":\"19171FDF6000AM\",\"appPackage\":\"org.wikipedia.alpha\",\"os_version\":\"12.0\",\"device\":\"google pixel 6\"},\"platformName\":\"Android\",\"deviceName\":\"19171FDF6000AM\",\"udid\":\"19171FDF6000AM\",\"automationName\":\"UIAutomator2\",\"os_version\":\"12.0\",\"device\":\"google pixel 6\",\"deviceApiLevel\":31,\"platformVersion\":\"12\",\"deviceScreenSize\":\"1080x2400\",\"deviceScreenDensity\":420,\"deviceModel\":\"Pixel 6\",\"deviceManufacturer\":\"Google\",\"pixelRatio\":2.625,\"statBarHeight\":124,\"viewportRect\":{\"left\":0,\"top\":124,\"width\":1080,\"height\":2116},\"lastScrollData\":null}}";

        [Fact]
        public void TheWholeTagIsBuiltFromARealBrowserStackSessionReply()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Tcps.Clear();
            tosca.Tcps["AppiumServer"] = "https://hub-cloud.browserstack.com/wd/hub";
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default(RealSessionCapabilities);

            ToscaMobileDriver driver = Build(tosca, deviceHttp: device);
            Metadata metadata = MetadataResolver.Resolve(driver, new Cache<string, object?>());
            Dictionary<string, object?> tag = metadata.GetTag();

            // The friendly name, not the UDID the session reports as deviceName.
            Assert.Equal("google pixel 6", tag["name"]);
            Assert.Equal("Android", tag["osName"]);
            Assert.Equal("12", tag["osVersion"]);
            Assert.Equal(1080, tag["width"]);
            Assert.Equal(2400, tag["height"]);
            Assert.Equal("portrait", tag["orientation"]);

            // The bars: top of the viewport is the status bar, and the navigation bar is what is left
            // over once the usable area and status bar are taken off the full height.
            Assert.Equal(124, metadata.StatBarHeight());
            Assert.Equal(2400 - (2116 + 124), metadata.NavBarHeight());
        }

        [Fact]
        public void TheUdidIsNotUsedAsTheDeviceName()
        {
            // Percy groups comparisons by this, so a UDID would give the device a baseline named after
            // a serial number — and on iOS, which reads deviceName first, that is what would happen.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Tcps.Clear();
            tosca.Tcps["AppiumServer"] = "https://hub-cloud.browserstack.com/wd/hub";
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default(RealSessionCapabilities);

            Assert.Equal("Google Pixel 6",
                Build(tosca, deviceHttp: device).Capabilities.GetString("deviceName"));
        }

        [Theory]
        [InlineData("{\"value\":{\"deviceScreenSize\":\"1080x2400\"}}")]
        [InlineData("{\"deviceScreenSize\":\"1080x2400\"}")]
        [InlineData("{\"value\":{\"capabilities\":{\"deviceScreenSize\":\"1080x2400\"}}}")]
        public void AnyOfTheThreeEnvelopesIsAccepted(string body)
        {
            // Which one arrives depends on the server. Requiring a particular one is what made a
            // working endpoint look like a failing one.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler().Default(body);

            Assert.Equal("1080x2400",
                Build(tosca, deviceHttp: device).Capabilities.GetString("deviceScreenSize"));
        }

        [Fact]
        public void TheBarsAreBuiltFromTheSessionsSystemBarsWhenNoViewportIsReported()
        {
            // The route that actually answers on Android. Without it nothing is cropped and the status
            // bar clock differs on every run — which reads as unstable snapshots, not a missing fact.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .On("/session/session-abc", "gone", System.Net.HttpStatusCode.NotFound)
                .On("/window/rect", "{\"value\":{\"width\":1080,\"height\":2400}}")
                .On("/execute/sync", "gone", System.Net.HttpStatusCode.NotFound)
                .On("/execute", "gone", System.Net.HttpStatusCode.NotFound)
                .Default("{\"value\":{\"statusBar\":{\"height\":72},\"navigationBar\":{\"height\":48}}}");

            ToscaMobileDriver driver = Build(tosca, deviceHttp: device);
            Metadata metadata = MetadataResolver.Resolve(driver, new Cache<string, object?>());

            Assert.Equal(72, metadata.StatBarHeight());
            Assert.Equal(48, metadata.NavBarHeight());
            Assert.Equal(1080, metadata.DeviceScreenWidth());
            Assert.Equal(2400, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void AReportedViewportIsPreferredOverTheBarHeights()
        {
            // It states the usable area directly rather than by subtraction.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .On("/session/session-abc", "gone", System.Net.HttpStatusCode.NotFound)
                .On("/window/rect", "{\"value\":{\"width\":1080,\"height\":2400}}")
                .Default("{\"value\":\"{\\\"top\\\":60,\\\"left\\\":0," +
                    "\\\"width\\\":1080,\\\"height\\\":2300}\"}");

            ToscaMobileDriver driver = Build(tosca, deviceHttp: device);
            Metadata metadata = MetadataResolver.Resolve(driver, new Cache<string, object?>());

            Assert.Equal(60, metadata.StatBarHeight());
            Assert.Equal(40, metadata.NavBarHeight());
        }

        [Fact]
        public void TheScreenSizeComesFromTheSessionWindowWhenNoCapabilityCarriesIt()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .On("/session/session-abc", "gone", System.Net.HttpStatusCode.NotFound)
                .Default("{\"value\":{\"width\":828,\"height\":1792}}");

            Assert.Equal("828x1792",
                Build(tosca, deviceHttp: device).Capabilities.GetString("deviceScreenSize"));
        }

        [Fact]
        public void NoViewportAndNoBarsIsWarnedAboutBecauseNothingWillBeCropped()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default("gone", System.Net.HttpStatusCode.NotFound);

            _ = Build(tosca, deviceHttp: device).Capabilities;

            Assert.True(Logged("clock in the status bar will differ between runs"));
            // Names no parameter to set: there is none, and an earlier version of this message sent
            // people looking for a StatusBarHeight row that does not exist.
            Assert.True(Logged("no parameter to supply them"));
        }

        [Fact]
        public void BarHeightsWithNoScreenSizeStillGiveTheStatusBar()
        {
            // Partial is better than nothing: the status bar is what causes the flake.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .On("/session/session-abc", "gone", System.Net.HttpStatusCode.NotFound)
                .On("/window/rect", "gone", System.Net.HttpStatusCode.NotFound)
                .On("/window/current/size", "gone", System.Net.HttpStatusCode.NotFound)
                .On("/execute/sync", "gone", System.Net.HttpStatusCode.NotFound)
                .On("/execute", "gone", System.Net.HttpStatusCode.NotFound)
                .Default("{\"value\":{\"statusBar\":{\"height\":72}}}");

            ToscaMobileDriver driver = Build(tosca, deviceHttp: device);
            Metadata metadata = MetadataResolver.Resolve(driver, new Cache<string, object?>());

            Assert.Equal(72, metadata.StatBarHeight());
        }

        [Fact]
        public void TheSessionsAnswerOutranksTheTestConfigurationsRequest()
        {
            // Both may have an opinion; the session describes the device that was actually allocated
            // while a parameter describes what was asked for.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default("{\"value\":{\"deviceName\":\"Google Pixel 7 Pro\"," +
                    "\"platformVersion\":\"14\"}}");

            ToscaMobileDriver driver = Build(tosca, deviceHttp: device);

            Assert.Equal("Google Pixel 7 Pro", driver.Capabilities.GetString("deviceName"));
            Assert.Equal("14", driver.Capabilities.GetString("platformVersion"));
        }

        [Fact]
        public void ACapabilityMapNestedOneLevelDeepIsUnwrapped()
        {
            // Appium answers with the map directly; some servers nest it under "capabilities".
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default("{\"value\":{\"capabilities\":{\"platformVersion\":\"15\"}}}");

            Assert.Equal("15",
                Build(tosca, deviceHttp: device).Capabilities.GetString("platformVersion"));
        }

        [Fact]
        public void TheOrientationIsAskedOfTheSessionWhenNoParameterCarriesOne()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .On("/session/session-abc", "{\"value\":{}}")
                .Default("{\"value\":\"LANDSCAPE\"}");

            Assert.Equal("LANDSCAPE", Build(tosca, deviceHttp: device).Orientation);
        }

        [Fact]
        public void TheWindowWidthIsAskedOfTheSession()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default("{\"value\":{\"width\":390,\"height\":844}}");

            Assert.Equal(390, Build(tosca, deviceHttp: device).WindowWidth);
        }

        [Fact]
        public void ASessionThatReportsNothingLeavesTheParametersInCharge()
        {
            // Degrades the tag rather than the run: every one of these has a module parameter that can
            // supply it instead.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = new StubHttpMessageHandler()
                .Default("nope", System.Net.HttpStatusCode.NotFound);
            ToscaMobileDriver driver = Build(tosca, deviceHttp: device);

            Assert.Equal("Google Pixel 7", driver.Capabilities.GetString("deviceName"));
            Assert.Equal(0, driver.WindowWidth);
            Assert.Null(driver.Orientation);
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
        public void ASessionWithNoParametersAtAllSaysWhatToCheck()
        {
            ToscaMobileDriver driver = Build(new StubToscaEnvironment());

            Assert.Empty(driver.Capabilities);
            Assert.Null(driver.Host);
            Assert.Null(driver.PlatformName);
            // The two things that are actually actionable. Device details have no module parameters
            // to fall back on — they come from the session — so pointing at one would be a dead end.
            Assert.True(Logged("SessionId parameter carries the Appium session id"));
            Assert.True(Logged("AppiumServer test configuration parameter"));
        }

        [Fact]
        public void ANullEnvironmentIsRefusedAtConstruction()
        {
            // Without it there is no route to a parameter or a buffer, so every later call would
            // NullReference somewhere less obvious than the constructor.
            Assert.Throws<ArgumentNullException>(() => new ToscaMobileDriver(null!));
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

            Assert.Single(Logs, entry => entry.Message.Contains("not supported on Tosca"));
        }
    }
}
