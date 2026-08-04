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

        public string? CaptureScreenshot(string directory, string fileName)
        {
            if (ReportPathWithoutWriting != null) return ReportPathWithoutWriting;
            if (ScreenshotBase64 == null) return null;

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
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
            StubToscaEnvironment tosca, ScreenshotOptions? options = null, string? buffer = null) =>
            new ToscaMobileDriver(tosca, options ?? new ScreenshotOptions(), buffer);

        [Fact]
        public void TheHubUrlComesFromTheAppiumServerParameter()
        {
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());

            Assert.Equal("https://hub-cloud.browserstack.com/wd/hub", driver.Host);
        }

        [Fact]
        public void AnAppAutomateHubIsRecognisedButDoesNotSelectTheRemoteCapturePath()
        {
            // This is the load-bearing consequence of the design: the host says App Automate, but a
            // Tosca session cannot send the browserstack_executor commands that provider is built
            // out of, so it must fall back to local capture rather than fail on its first command.
            SetEnv("PERCY_LOGLEVEL", "debug");
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());

            Assert.Contains("browserstack", driver.Host!);
            Assert.False(driver.CanExecuteScript);
            Assert.False(AppAutomate.Supports(driver));
            Assert.True(Logged("captured locally"));
        }

        [Fact]
        public void ProviderResolutionPicksLocalCaptureForAToscaSession()
        {
            ToscaMobileDriver driver = Build(StubToscaEnvironment.AppAutomate());
            GenericProvider provider = ProviderResolver.ResolveProvider(driver,
                new PercyClient(new StubHttpMessageHandler().Client(), "http://localhost:5338"),
                new Cache<string, object?>());

            Assert.IsNotType<AppAutomate>(provider);
        }

        [Fact]
        public void RemoteCaptureBecomesAvailableIfToscaEverAllowsScripting()
        {
            // Not speculative plumbing for its own sake: the whole reason CanExecuteScript is asked
            // rather than hard-coded is so this needs no code change when it becomes possible.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            tosca.CanExecuteScript = true;
            ToscaMobileDriver driver = Build(tosca);

            Assert.True(AppAutomate.Supports(driver));
            driver.ExecuteScript("browserstack_executor: {}");
            Assert.Equal(new[] { "browserstack_executor: {}" }, tosca.Scripts);
        }

        [Fact]
        public void ScriptsAreNotEvenAttemptedWhenScriptingIsUnavailable()
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
            // Percy on Automate posts this id so the CLI can reconnect; handing it a placeholder
            // would fail inside the CLI with an error that never mentions the missing buffer.
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
            // Percy on Automate forwards this dictionary to the CLI, which understands more of the
            // connection details than this SDK does — dropping the unmapped ones loses information.
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

        [Fact]
        public void TheWindowWidthIsUnavailableAndReportsZero()
        {
            // Only iOS's scale factor reads it, and that degrades to 1 — correct whenever the real
            // and logical widths match.
            Assert.Equal(0, Build(StubToscaEnvironment.AppAutomate()).WindowWidth);
        }

        [Fact]
        public void AScreenshotIsCapturedThroughTheEngineAndReturnedAsBase64()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            SetEnv("PERCY_TMP_DIR", tempDir);
            try
            {
                StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();

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
                string path = tosca.CaptureScreenshot(tempDir, "scratch.png")!;
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
            try
            {
                StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
                tosca.Tcps["ScreenResolution"] = "1080x2340";
                StubHttpMessageHandler handler = new StubHttpMessageHandler()
                    .On("/percy/healthcheck", "{\"success\":true,\"build\":{\"id\":\"b\",\"url\":\"u\"}}")
                    .Default("{\"success\":true,\"data\":{\"link\":\"https://percy.io/c/1\"}}");
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

        [Fact]
        public void APercyOnAutomateSnapshotEndToEndSendsTheSessionAndHub()
        {
            // The recommended path on Tosca: nothing is captured locally, so none of the
            // unavailable driver capabilities matter.
            StubToscaEnvironment tosca = StubToscaEnvironment.AppAutomate();
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck",
                    "{\"success\":true,\"type\":\"automate\",\"build\":{\"id\":\"b\",\"url\":\"u\"}}")
                .Default("{\"success\":true,\"data\":{\"link\":\"https://percy.io/c/2\"}}");
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            ToscaMobileDriver driver = Build(tosca);

            Assert.NotNull(new PercyOnAutomate(driver, client).Screenshot("cart"));

            string body = handler.BodyFor("/percy/automateScreenshot")!;
            Assert.Contains("\"sessionId\":\"session-abc\"", body);
            Assert.Contains("\"commandExecutorUrl\":\"https://hub-cloud.browserstack.com/wd/hub\"", body);
            Assert.Contains("\"deviceName\":\"Google Pixel 7\"", body);
        }
    }
}
