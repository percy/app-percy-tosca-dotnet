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

        /// <summary>Base64 written to the captured file; null makes capture fail.</summary>
        public string? ScreenshotBase64 { get; set; } = StubMobileDriver.ValidPngBase64;

        /// <summary>Set to report a path the engine never actually wrote.</summary>
        public string? ReportPathWithoutWriting { get; set; }

        public bool CanExecuteScript { get; set; }
        public string? ScriptResult { get; set; }
        public List<string> Scripts { get; } = new List<string>();
        public List<string> Captured { get; } = new List<string>();

        public string? TestConfigurationParameter(string name) =>
            Tcps.TryGetValue(name, out string? value) ? value : null;

        public IReadOnlyDictionary<string, string?> TestConfigurationParameters() => Tcps;

        public string? Buffer(string name) =>
            Buffers.TryGetValue(name, out string? value) ? value : null;

        /// <summary>
        /// Where the file lands is the shim's business on Tosca — the mobile engine's own task reads
        /// its destination from module parameters — so the stub picks a directory the same way a
        /// module would.
        /// </summary>
        public string Directory_ { get; set; } = Path.GetTempPath();

        public string? CaptureScreenshot()
        {
            if (ReportPathWithoutWriting != null) return ReportPathWithoutWriting;
            if (ScreenshotBase64 == null) return null;

            Directory.CreateDirectory(Directory_);
            string path = Path.Combine(Directory_, $"percy-stub-{Guid.NewGuid()}.png");
            File.WriteAllBytes(path, Convert.FromBase64String(ScreenshotBase64));
            Captured.Add(path);
            return path;
        }

        public string? ExecuteScript(string script)
        {
            Scripts.Add(script);
            return ScriptResult;
        }

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
            StubHttpMessageHandler? deviceHttp = null, Func<string?, string?>? discover = null) =>
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
        public void ToscasOwnScriptingIsStillUsedIfItEverBecomesAvailable()
        {
            // Kept as a fallback rather than removed: a future Tosca that does pass scripts through
            // should need no change here.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.CanExecuteScript = true;
            ToscaMobileDriver driver = Build(tosca);

            Assert.True(driver.CanExecuteScript);
            driver.ExecuteScript("browserstack_executor: {}");
            Assert.Equal(new[] { "browserstack_executor: {}" }, tosca.Scripts);
        }

        [Fact]
        public void ScriptsAreNotAttemptedWhenThereIsNoRouteForThem()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();

            Assert.Null(Build(tosca).ExecuteScript("mobile: viewportRect"));
            Assert.Empty(tosca.Scripts);
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

            ToscaMobileDriver driver = Build(tosca, discover: _ => "discovered-1");

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

            ToscaMobileDriver driver = Build(tosca, discover: _ => { asked = true; return "discovered"; });

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

            ToscaMobileDriver driver = Build(tosca, discover: _ => { calls++; return null; });

            Assert.False(driver.HasRealSessionId);
            _ = driver.SessionId;
            _ = driver.SessionId;
            Assert.Equal(1, calls);
        }

        [Fact]
        public void DiscoveryIsHandedTheHubUrlToAuthenticateWith()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = null;
            string? seen = null;

            _ = Build(tosca, discover: hub => { seen = hub; return null; }).SessionId;

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
            // Preferred over Tosca's own screenshot task: it depends only on the WebDriver standard,
            // not on a task name that differs between Tosca releases.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler device = DeviceServing(StubMobileDriver.ValidPngBase64);

            Assert.Equal(StubMobileDriver.ValidPngBase64,
                Build(tosca, deviceHttp: device).GetScreenshotBase64());

            Assert.Contains("/session/session-abc/screenshot", device.Requests[0].Url);
            // Tosca's task was never asked, so a wrong task name cannot break this path.
            Assert.Empty(tosca.Captured);
        }

        [Fact]
        public void ADeviceSessionThatRefusesFallsBackToToscasOwnTask()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            try
            {
                StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
                tosca.Directory_ = tempDir;
                StubHttpMessageHandler device = new StubHttpMessageHandler()
                    .Default("no session", System.Net.HttpStatusCode.NotFound);

                Assert.Equal(StubMobileDriver.ValidPngBase64,
                    Build(tosca, deviceHttp: device).GetScreenshotBase64());

                Assert.True(Logged("refused a screenshot"));
                Assert.Single(tosca.Captured);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void WithoutASessionIdTheDeviceIsNotAskedAndTheModuleIsNamed()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.Buffers[ToscaMobileDriver.DefaultSessionIdBuffer] = null;
            StubHttpMessageHandler device = DeviceServing(StubMobileDriver.ValidPngBase64);

            Build(tosca, deviceHttp: device).GetScreenshotBase64();

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

            Build(tosca, deviceHttp: device).GetScreenshotBase64();

            Assert.Empty(device.Requests);
            Assert.True(Logged("No AppiumServer"));
        }

        [Fact]
        public void AScreenshotIsCapturedThroughTheEngineAndReturnedAsBase64()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            SetEnv("PERCY_TMP_DIR", tempDir);
            try
            {
                StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
                tosca.Directory_ = tempDir;

                Assert.Equal(StubMobileDriver.ValidPngBase64, Build(tosca).GetScreenshotBase64());

                // The engine's scratch copy is cleaned up; only the tile the CLI reads survives.
                Assert.Single(tosca.Captured);
                Assert.False(File.Exists(tosca.Captured[0]));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void AnEngineThatProducesNoScreenshotSaysWhatToCheck()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.ScreenshotBase64 = null;

            PercyException error = Assert.Throws<PercyException>(
                () => Build(tosca).GetScreenshotBase64());
            Assert.Contains("did not produce a screenshot", error.Message);
            Assert.Contains("Mobile Engine 3.0 server", error.Message);
            // Names the two module rows the engine's task needs, since a missing one is the likeliest
            // cause and nothing else in the log would say so.
            Assert.Contains("Directory and Filename", error.Message);
        }

        [Fact]
        public void AReportedPathWithNoFileBehindItIsNamed()
        {
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.ReportPathWithoutWriting = Path.Combine(Path.GetTempPath(), "percy-never-written.png");

            PercyException error = Assert.Throws<PercyException>(
                () => Build(tosca).GetScreenshotBase64());
            Assert.Contains("percy-never-written.png", error.Message);
            Assert.Contains("no file is there", error.Message);
        }

        [Fact]
        public void ADirectoryAtTheReportedPathIsTreatedAsNoFile()
        {
            // File.Exists is false for a directory, so this lands on the "no file is there" branch
            // rather than attempting to read it — which is the right outcome, and worth pinning
            // because the alternative is an IOException with no mention of the path.
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                string asDirectory = Path.Combine(tempDir, "not-a-file.png");
                Directory.CreateDirectory(asDirectory);
                StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
                tosca.ReportPathWithoutWriting = asDirectory;

                PercyException error = Assert.Throws<PercyException>(
                    () => Build(tosca).GetScreenshotBase64());
                Assert.Contains("no file is there", error.Message);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void AnUndeletableScratchFileDoesNotFailTheSnapshot()
        {
            // Deleting the engine's scratch copy is best-effort: the tile the CLI reads is written
            // separately, so a file we cannot remove costs a stray temp file and must not fail an
            // otherwise-good snapshot.
            //
            // Made undeletable by removing write permission from the containing directory, which is
            // what governs deletion on Unix. Windows does not work that way, so the test declares
            // itself inapplicable there rather than asserting something untrue.
            // Returning rather than skipping avoids a test-framework extension package for one
            // case; CI runs on Linux, where the assertions below do apply.
            if (OperatingSystem.IsWindows()) return;

            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            SetEnv("PERCY_TMP_DIR", tempDir);
            SetEnv("PERCY_LOGLEVEL", "debug");
            Directory.CreateDirectory(tempDir);
            try
            {
                StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
                tosca.Directory_ = tempDir;
                string path = tosca.CaptureScreenshot()!;
                tosca.ReportPathWithoutWriting = path;
                File.SetUnixFileMode(tempDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

                // The screenshot still comes back; only the cleanup failed.
                Assert.Equal(StubMobileDriver.ValidPngBase64, Build(tosca).GetScreenshotBase64());
                Assert.True(Logged("Could not delete the temporary screenshot"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    File.SetUnixFileMode(tempDir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Directory.Delete(tempDir, true);
                }
            }
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

        [Fact]
        public void ASnapshotEndToEndPostsATileWithTheDeviceTagFromTheTestConfiguration()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            SetEnv("PERCY_TMP_DIR", tempDir);
            // The point here is the tag built from test configuration parameters, not the executor, so
            // capture takes the local-tile path.
            SetEnv("PERCY_DISABLE_REMOTE_UPLOADS", "true");
            try
            {
                StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
                tosca.Directory_ = tempDir;
                tosca.Tcps["ScreenResolution"] = "1080x2340";
                StubHttpMessageHandler handler = new StubHttpMessageHandler()
                    .On("/percy/healthcheck", "{\"success\":true,\"build\":{\"id\":\"b\",\"url\":\"u\"}}")
                    .Default("{\"success\":true,\"link\":\"https://percy.io/c/1\",\"data\":{\"id\":\"c1\"}}");
                PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");

                ScreenshotOptions options = new ScreenshotOptions { StatusBarHeight = 60, NavBarHeight = 40 };
                ToscaMobileDriver driver = Build(tosca, options);

                Assert.NotNull(new AppPercy(driver, client).Screenshot("home", options));

                string body = handler.BodyFor("/percy/comparison")!;
                Assert.Contains("\"name\":\"Google Pixel 7\"", body);
                Assert.Contains("\"osName\":\"Android\"", body);
                Assert.Contains("\"osVersion\":\"13.0\"", body);
                Assert.Contains("\"width\":1080", body);
                Assert.Contains("\"height\":2340", body);
                Assert.Contains("\"statusBarHeight\":60", body);
                Assert.Contains($"\"clientInfo\":\"{Env.ClientInfo}\"", body);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

    }
}
