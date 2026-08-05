using System.Text.Json;
using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class ProviderResolverTests : CoreTestBase
    {
        private static GenericProvider Resolve(StubMobileDriver driver) =>
            ProviderResolver.ResolveProvider(driver,
                new PercyClient(new StubHttpMessageHandler().Client(), "http://localhost:5338"),
                new Cache<string, object?>());

        [Fact]
        public void ABrowserStackHostSelectsTheAppAutomateProvider()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "https://hub-cloud.browserstack.com/wd/hub";

            Assert.IsType<AppAutomate>(Resolve(driver));
        }

        [Fact]
        public void AnyOtherHostSelectsTheLocalCaptureProvider()
        {
            StubMobileDriver local = StubMobileDriver.Android();
            local.Host = "http://127.0.0.1:4723/wd/hub";
            Assert.IsType<GenericProvider>(Resolve(local));
            Assert.IsNotType<AppAutomate>(Resolve(local));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ASessionWithNoHostIsNotAppAutomate(string? host)
        {
            // Keyed on the host because it is the only signal available before any command is sent.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = host;

            Assert.False(AppAutomate.Supports(driver));
        }

        [Fact]
        public void TheAutomateDomainCanBePointedAtAPrivateHub()
        {
            SetEnv("AA_DOMAIN", "my-hub.internal");
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "https://my-hub.internal/wd/hub";

            Assert.True(AppAutomate.Supports(driver));
        }

        [Fact]
        public void TheHostIsMatchedCaseInsensitively()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "https://HUB-CLOUD.BROWSERSTACK.COM/wd/hub";

            Assert.True(AppAutomate.Supports(driver));
        }
    }

    public class GenericProviderTests : CoreTestBase
    {
        private const string Accepted = "{\"success\":true,\"link\":\"https://percy.io/c/1\",\"data\":{\"id\":\"c1\"}}";

        private static (GenericProvider Provider, StubHttpMessageHandler Handler) Build(
            StubMobileDriver driver)
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default(Accepted);
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            return (new GenericProvider(driver, client, new Cache<string, object?>()), handler);
        }

        [Fact]
        public void ASnapshotPostsATileWrittenToDiskAlongsideTheDeviceTag()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            SetEnv("PERCY_TMP_DIR", tempDir);
            try
            {
                (GenericProvider provider, StubHttpMessageHandler handler) =
                    Build(StubMobileDriver.Android());

                JsonElement? data = provider.Screenshot("home", new ScreenshotOptions());

                // The provider returns the CLI's whole response, so `link` — a sibling of `data` —
                // is still reachable. That is what lets App Automate report the comparison URL.
                Assert.Equal("https://percy.io/c/1", Json.PropertyAsString(data, "link"));
                Assert.Equal("c1", Json.PropertyAsString(Json.Property(data, "data"), "id"));

                string body = handler.BodyFor("/percy/comparison")!;
                Assert.Contains("\"name\":\"home\"", body);
                Assert.Contains("\"osName\":\"Android\"", body);
                Assert.Contains("\"statusBarHeight\":60", body);
                Assert.Contains("\"navBarHeight\":40", body);

                // The CLI reads the tile from disk by path, so the file must actually be there and
                // hold the decoded image rather than the base64 text.
                string[] written = Directory.GetFiles(tempDir, "percy-*.png");
                Assert.Single(written);
                Assert.Equal(Convert.FromBase64String(StubMobileDriver.ValidPngBase64),
                    File.ReadAllBytes(written[0]));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void TheTempDirectoryIsCreatedWhenItDoesNotExistYet()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid(), "nested");
            SetEnv("PERCY_TMP_DIR", tempDir);
            try
            {
                (GenericProvider provider, _) = Build(StubMobileDriver.Android());
                provider.Screenshot("home", new ScreenshotOptions());
                Assert.True(Directory.Exists(tempDir));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void AnEmptyScreenshotIsReportedAsSomethingActionable()
        {
            // Convert.FromBase64String("") succeeds and would write a 0-byte PNG the CLI then
            // rejects with a much less useful message.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Screenshot = "";
            (GenericProvider provider, _) = Build(driver);

            PercyException error = Assert.Throws<PercyException>(
                () => provider.Screenshot("home", new ScreenshotOptions()));
            Assert.Contains("empty screenshot", error.Message);
        }

        [Fact]
        public void FullPageIsAnnouncedAsUnavailableRatherThanSilentlyDowngraded()
        {
            // Otherwise the user believes they got a full-page snapshot and does not.
            (GenericProvider provider, _) = Build(StubMobileDriver.Android());

            provider.Screenshot("home", new ScreenshotOptions { FullPage = true });

            Assert.True(Logged("only supported on App Automate"));
        }

        [Fact]
        public void APlatformVersionFromTheCallerFillsInWhatTheStepDidNotSay()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("platformVersion");
            (GenericProvider provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions(), "12");

            Assert.Contains("\"osVersion\":\"12\"", handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void AnExplicitOsVersionIsNotOverriddenByTheCaller()
        {
            (GenericProvider provider, StubHttpMessageHandler handler) =
                Build(StubMobileDriver.Android());

            provider.Screenshot("home", new ScreenshotOptions { PlatformVersion = "14" }, "12");

            Assert.Contains("\"osVersion\":\"14\"", handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void RegionsResolveToDevicePixelCoordinates()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.ElementsByXPath["//total"] = new ElementRect(10, 20, 100, 50);
            driver.ElementsByAccessibilityId["banner"] = new ElementRect(0, 0, 200, 30);
            (GenericProvider provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                IgnoreRegionXpaths = new List<string> { "//total" },
                ConsiderRegionAccessibilityIds = new List<string> { "banner" }
            });

            string body = handler.BodyFor("/percy/comparison")!;
            // Android's scale factor is 1, so coordinates pass through unscaled.
            Assert.Contains("\"selector\":\"xpath: //total\"", body);
            Assert.Contains("\"top\":20,\"bottom\":70,\"left\":10,\"right\":110", body);
            Assert.Contains("\"selector\":\"id: banner\"", body);
        }

        [Fact]
        public void RegionCoordinatesAreScaledOnADeviceWithAScaleFactor()
        {
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.Caps["viewportRect"] = new Dictionary<string, object?>
            {
                ["top"] = 44,
                ["width"] = 1170,
                ["height"] = 2488
            };
            driver.WindowWidth = 390;
            driver.ElementsByXPath["//x"] = new ElementRect(10, 20, 30, 40);
            (GenericProvider provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                IgnoreRegionXpaths = new List<string> { "//x" }
            });

            // Scale factor 3: the session reports points but the diffed screenshot is in pixels.
            Assert.Contains("\"top\":60,\"bottom\":180,\"left\":30,\"right\":120",
                handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void ALocatorThatMatchesNothingIsSkippedRatherThanFailingTheSnapshot()
        {
            // A sheet that declares one ignore region and reuses it across screens will legitimately
            // hit screens where the element is absent.
            (GenericProvider provider, StubHttpMessageHandler handler) =
                Build(StubMobileDriver.Android());

            provider.Screenshot("home", new ScreenshotOptions
            {
                IgnoreRegionXpaths = new List<string> { "//missing" },
                IgnoreRegionAccessibilityIds = new List<string> { "absent" }
            });

            Assert.True(Logged("//missing"));
            Assert.True(Logged("absent"));
            Assert.Contains("\"ignoreElementsData\":[]", handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void ALocatorLookupThatThrowsIsAlsoSkipped()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.FindElementError = new InvalidOperationException("stale element");
            (GenericProvider provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                IgnoreRegionXpaths = new List<string> { "//x" }
            });

            Assert.True(Logged("//x"));
        }

        [Fact]
        public void CustomRegionsArePassedThroughUnscaled()
        {
            // These are declared in device pixels already, so scaling them would double-apply.
            (GenericProvider provider, StubHttpMessageHandler handler) =
                Build(StubMobileDriver.Android());

            provider.Screenshot("home", new ScreenshotOptions
            {
                CustomIgnoreRegions = new List<Region> { new Region(0, 100, 0, 200) }
            });

            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains("\"selector\":\"custom region 0\"", body);
            Assert.Contains("\"top\":0,\"bottom\":100,\"left\":0,\"right\":200", body);
        }

        /// <summary>
        /// Base64 of a PNG header declaring the given size. Only the first 24 bytes are ever read, so
        /// this is a complete stand-in for a real screenshot as far as measurement is concerned.
        /// </summary>
        private static string FakePng(int width, int height)
        {
            byte[] bytes = new byte[24];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
            bytes[11] = 13;                                  // IHDR chunk length
            System.Text.Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
            BitConverter.GetBytes(width).Reverse().ToArray().CopyTo(bytes, 16);
            BitConverter.GetBytes(height).Reverse().ToArray().CopyTo(bytes, 20);
            return Convert.ToBase64String(bytes);
        }

        [Fact]
        public void TheScreenSizeIsMeasuredFromTheScreenshotWhenNothingElseKnowsIt()
        {
            // The normal case on Tosca: no screen-size capability and a device absent from the static
            // table. The screenshot itself states the size, so the user should not have to type it.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("deviceScreenSize");
            driver.Caps.Remove("viewportRect");
            driver.Screenshot = FakePng(1080, 2340);
            (GenericProvider provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains("\"width\":1080", body);
            Assert.Contains("\"height\":2340", body);
            Assert.False(Logged("Could not determine the device screen size"));
        }

        [Fact]
        public void AnExplicitScreenSizeStillWinsOverTheMeasuredOne()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("deviceScreenSize");
            driver.Caps.Remove("viewportRect");
            driver.Screenshot = FakePng(1080, 2340);
            (GenericProvider provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home",
                new ScreenshotOptions { ScreenWidth = 720, ScreenHeight = 1280 });

            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains("\"width\":720", body);
            Assert.Contains("\"height\":1280", body);
        }

        [Fact]
        public void ASessionReportedSizeStillWinsOverTheMeasuredOne()
        {
            // deviceScreenSize describes the whole screen; a screenshot may be cropped, so the
            // capability is the better source when present.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Screenshot = FakePng(999, 999);
            (GenericProvider provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.Contains("\"width\":1080", handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void CustomRegionsSurviveAnUnmeasurableScreenInsteadOfBeingDiscarded()
        {
            // Validating against a 0x0 screen rejects every region, so the user loses the only region
            // type available on this path — and the message blames the region rather than the missing
            // dimensions. Reached when the capture is not a readable PNG.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("deviceScreenSize");
            driver.Caps.Remove("viewportRect");
            driver.Screenshot = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
            (GenericProvider provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                CustomIgnoreRegions = new List<Region> { new Region(0, 100, 0, 200) }
            });

            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains("\"top\":0,\"bottom\":100,\"left\":0,\"right\":200", body);
            Assert.False(Logged("is not valid"));
        }

        [Fact]
        public void AnUnmeasurableScreenIsStillReportedBecauseItCorruptsTheComparisonTag()
        {
            // Percy groups and diffs by the tag, so a 0x0 tag will not group with correctly-tagged
            // snapshots. Now only reachable when the capture is not a readable PNG, since otherwise
            // the size is measured from it.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("deviceScreenSize");
            driver.Caps.Remove("viewportRect");
            driver.Screenshot = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
            (GenericProvider provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.True(Logged("ScreenWidth and ScreenHeight"));
        }

        [Fact]
        public void AKnownScreenSizeIsNotWarnedAbout()
        {
            (GenericProvider provider, _) = Build(StubMobileDriver.Android());

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.False(Logged("Could not determine the device screen size"));
        }

        [Fact]
        public void ACustomRegionOutsideTheScreenIsReportedAndSkipped()
        {
            (GenericProvider provider, StubHttpMessageHandler handler) =
                Build(StubMobileDriver.Android());

            provider.Screenshot("home", new ScreenshotOptions
            {
                CustomConsiderRegions = new List<Region> { new Region(0, 99999, 0, 200) }
            });

            Assert.True(Logged("is not valid"));
            Assert.Contains("\"considerElementsData\":[]", handler.BodyFor("/percy/comparison")!);
        }
    }

    public class AppAutomateTests : CoreTestBase
    {
        private const string Accepted = "{\"success\":true,\"link\":\"https://percy.io/c/1\",\"data\":{\"id\":\"c1\"}}";

        private const string BeginResult =
            "{\"success\":true,\"deviceName\":\"Samsung Galaxy S23\",\"osVersion\":\"13.0\"," +
            "\"buildHash\":\"bh\",\"sessionHash\":\"sh\"}";

        private const string TileResult =
            "{\"success\":true,\"result\":\"[{\\\"sha\\\":\\\"abc123-1\\\"," +
            "\\\"header_height\\\":10,\\\"footer_height\\\":20}]\"}";

        /// <summary>Captures one snapshot and reports the screenshotType the hub was asked for.</summary>
        private static string RequestedScreenshotType(ScreenshotOptions options)
        {
            StubMobileDriver driver = CapturingDriver();
            Build(driver).Provider.Screenshot("snap", options);
            string request = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"screenshot\""));
            return Json.PropertyAsString(
                Json.Property(Json.TryParse(request.Substring(request.IndexOf('{'))), "arguments"),
                "screenshotType")!;
        }

        private static StubMobileDriver AutomateDriver()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "https://hub-cloud.browserstack.com/wd/hub";
            return driver;
        }

        private static (AppAutomate Provider, StubHttpMessageHandler Handler) Build(
            StubMobileDriver driver)
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default(Accepted);
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            return (new AppAutomate(driver, client, new Cache<string, object?>()), handler);
        }

        [Fact]
        public void TilesComeBackAsContentHashesSoNoImageDataPassesThroughTosca()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            driver.ScriptReplies.Add(("\"state\":\"end\"", "{\"success\":true}"));
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            string body = handler.BodyFor("/percy/comparison")!;
            // The sha arrives suffixed as "<sha>-<n>"; only the hash is the tile key.
            Assert.Contains("\"sha\":\"abc123\"", body);
            Assert.Contains("\"headerHeight\":10", body);
            Assert.Contains("\"footerHeight\":20", body);
            Assert.Contains("\"filepath\":null", body);
        }

        [Fact]
        public void TheExecutorsDeviceAndOsVersionAreUsedOverTheCapabilities()
        {
            // The hub knows the device it actually allocated; the capability is only what was asked
            // for.
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains("\"name\":\"Samsung Galaxy S23\"", body);
            Assert.Contains("\"osVersion\":\"13\"", body);
        }

        [Fact]
        public void AnExplicitDeviceNameIsNotOverriddenByTheExecutor()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions { DeviceName = "My Device" });

            Assert.Contains("\"name\":\"My Device\"", handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void TheSessionIsLinkedToItsAppAutomateDashboardUrl()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.Contains(
                "\"externalDebugUrl\":\"https://app-automate.browserstack.com/dashboard/v2/builds/bh/sessions/sh\"",
                handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void WithoutBuildAndSessionHashesThereIsNoDebugUrl()
        {
            (AppAutomate provider, _) = Build(AutomateDriver());

            Assert.Null(provider.GetDebugUrl(null));
            Assert.Null(provider.GetDebugUrl(Json.TryParse("{\"buildHash\":\"bh\"}")));
            Assert.Null(provider.GetDebugUrl(Json.TryParse("{\"sessionHash\":\"sh\"}")));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"osVersion\":\"\"}")]
        public void AnAbsentOsVersionYieldsNull(string body)
        {
            (AppAutomate provider, _) = Build(AutomateDriver());
            Assert.Null(provider.OsVersion(Json.TryParse(body)));
        }

        [Fact]
        public void TheBuildIsStampedOntoTheBeginMarkerSoPercyAndAppAutomateAreLinked()
        {
            Env.PercyBuildId = "build-7";
            Env.PercyBuildUrl = "https://percy.io/b/7";
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", "{\"success\":true}"));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            string begin = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"begin\""));
            Assert.StartsWith("browserstack_executor: ", begin);
            Assert.Contains("\"percyBuildId\":\"build-7\"", begin);
            Assert.Contains("\"percyBuildUrl\":\"https://percy.io/b/7\"", begin);
            Assert.Contains("\"name\":\"home\"", begin);
        }

        [Fact]
        public void TheOutcomeIsReportedBackToTheHub()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions { Sync = true });

            string end = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"end\""));
            Assert.Contains("\"status\":\"success\"", end);
            Assert.Contains("\"percyScreenshotUrl\":\"https://percy.io/c/1\"", end);
            Assert.Contains("\"sync\":true", end);
        }

        [Fact]
        public void AFailedCaptureIsReportedToTheHubAsAFailureAndStillPropagates()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", "{\"success\":false,\"message\":\"quota\"}"));
            (AppAutomate provider, _) = Build(driver);

            Assert.Throws<PercyException>(() => provider.Screenshot("home", new ScreenshotOptions()));

            string end = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"end\""));
            Assert.Contains("\"status\":\"failure\"", end);
            Assert.Contains("quota", end);
        }

        [Fact]
        public void CredentialsAreRedactedFromTheStatusMessagePersistedInTheHubLog()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptError = null;
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"",
                "{\"success\":false,\"message\":\"failed against https://user:key@hub.browserstack.com\"}"));
            (AppAutomate provider, _) = Build(driver);

            Assert.Throws<PercyException>(() => provider.Screenshot("home", new ScreenshotOptions()));

            string end = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"end\""));
            Assert.DoesNotContain("user:key", end);
            Assert.Contains("***@hub.browserstack.com", end);
        }

        [Fact]
        public void ARefusedBeginStopsTheRemainingExecutorCallsForTheRun()
        {
            // Once the hub has said it will not serve percyScreenshot, re-issuing the commands only
            // adds latency to every remaining step.
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", "{\"success\":false}"));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.DoesNotContain(driver.ExecutedScripts, s => s.Contains("\"state\":\"end\""));
        }

        [Fact]
        public void AThrowingBeginIsLoggedAndDoesNotStopTheCapture()
        {
            StubMobileDriver driver = AutomateDriver();
            (AppAutomate provider, _) = Build(driver);
            driver.ScriptError = new InvalidOperationException("hub unreachable");

            Assert.Null(provider.ExecutePercyScreenshotBegin("home"));
            Assert.True(Logged("failed at percyScreenshot begin"));
        }

        [Fact]
        public void AThrowingEndIsLoggedRatherThanSwallowed()
        {
            // End is what writes the status into the hub session log, so losing why it failed
            // removes the last record of a failed screenshot.
            StubMobileDriver driver = AutomateDriver();
            (AppAutomate provider, _) = Build(driver);
            driver.ScriptError = new InvalidOperationException("hub unreachable");

            Assert.Null(provider.ExecutePercyScreenshotEnd("home", null, null, null));
            Assert.True(Logged("failed at percyScreenshot end"));
        }

        [Fact]
        public void FullPageIsRequestedOnlyWhenItWasAskedForWithEnoughScreens()
        {
            Assert.Equal("singlepage", RequestedScreenshotType(new ScreenshotOptions()));
            Assert.Equal("fullpage",
                RequestedScreenshotType(new ScreenshotOptions { FullPage = true, ScreenLengths = 4 }));
            // No screen count at all still means full page; the hub decides how many to take.
            Assert.Equal("fullpage",
                RequestedScreenshotType(new ScreenshotOptions { FullPage = true }));
            // One screen is not a full page, so asking for it would waste a hub round trip.
            Assert.Equal("singlepage",
                RequestedScreenshotType(new ScreenshotOptions { FullPage = true, ScreenLengths = 1 }));
        }

        [Fact]
        public void TheScreenshotRequestCarriesTheScrollingAndOffsetOptions()
        {
            StubMobileDriver driver = CapturingDriver();
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                FullPage = true,
                ScreenLengths = 3,
                ScrollableXpath = "//scroll",
                ScrollableId = "list",
                TopScrollviewOffset = 5,
                BottomScrollviewOffset = 6,
                IosOptimizedFullpage = true
            });

            string request = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"screenshot\""));
            Assert.Contains("\"numOfTiles\":3", request);
            // Misspelled on the wire; that is the hub's key name, not a typo here.
            Assert.Contains("\"scollableXpath\":\"//scroll\"", request);
            Assert.Contains("\"scrollableId\":\"list\"", request);
            Assert.Contains("\"topScrollviewOffset\":5", request);
            Assert.Contains("\"bottomScrollviewOffset\":6", request);
            Assert.Contains("\"iosOptimizedFullpage\":true", request);
            Assert.Contains("\"deviceHeight\":2340", request);
            Assert.Contains("\"scaleFactor\":1", request);
            Assert.Contains("\"projectId\":\"percy-prod\"", request);
        }

        [Fact]
        public void TheDevProjectIsSelectableForPercyDevelopment()
        {
            SetEnv("PERCY_ENABLE_DEV", "true");
            StubMobileDriver driver = CapturingDriver();
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.Contains("\"projectId\":\"percy-dev\"",
                driver.ExecutedScripts.First(s => s.Contains("\"state\":\"screenshot\"")));
        }

        [Fact]
        public void ForceFullPageIsForwardedToTheHub()
        {
            SetEnv("FORCE_FULL_PAGE", "true");
            StubMobileDriver driver = CapturingDriver();
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.Contains("\"FORCE_FULL_PAGE\":true",
                driver.ExecutedScripts.First(s => s.Contains("\"state\":\"screenshot\"")));
        }

        [Fact]
        public void ARefusalNamesWhatTheHubSaidRatherThanThrowingANullReference()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"",
                "{\"success\":false,\"message\":\"device busy\"}"));
            (AppAutomate provider, _) = Build(driver);

            PercyException error = Assert.Throws<PercyException>(
                () => provider.Screenshot("home", new ScreenshotOptions()));
            Assert.Contains("refused by BrowserStack", error.Message);
            Assert.Contains("device busy", error.Message);
        }

        [Fact]
        public void ASuccessMissingItsResultIsNotMisreportedAsARefusal()
        {
            // Saying "refused" would send users looking for a permission problem they do not have.
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", "{\"success\":true}"));
            (AppAutomate provider, _) = Build(driver);

            PercyException error = Assert.Throws<PercyException>(
                () => provider.Screenshot("home", new ScreenshotOptions()));
            Assert.Contains("returned no result", error.Message);
            Assert.DoesNotContain("refused", error.Message);
        }

        [Fact]
        public void AnUnparseableTileArrayNamesWhatCouldNotBeRead()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"",
                "{\"success\":true,\"result\":\"not-an-array\"}"));
            (AppAutomate provider, _) = Build(driver);

            PercyException error = Assert.Throws<PercyException>(
                () => provider.Screenshot("home", new ScreenshotOptions()));
            Assert.Contains("Could not parse the tile data", error.Message);
            Assert.Contains("not-an-array", error.Message);
        }

        [Fact]
        public void AnAlreadyDecodedTileArrayIsAcceptedToo()
        {
            // The hub double-encodes `result` as a JSON string, but accepting the bare array as
            // well costs nothing and avoids a hard failure if that ever changes.
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"",
                "{\"success\":true,\"result\":[{\"sha\":\"xyz-1\",\"header_height\":0,\"footer_height\":0}]}"));
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.Contains("\"sha\":\"xyz\"", handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void DisablingRemoteUploadsFallsBackToLocalCapture()
        {
            SetEnv("PERCY_DISABLE_REMOTE_UPLOADS", "true");
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            // A local tile, so a filepath and no sha — and no capture command was sent to the hub.
            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains("percy-", body);
            Assert.Contains("\"sha\":null", body);
            Assert.DoesNotContain(driver.ExecutedScripts, s => s.Contains("\"state\":\"screenshot\""));
        }

        [Fact]
        public void FullPageIsAnnouncedAsIncompatibleWithDisabledRemoteUploads()
        {
            SetEnv("PERCY_DISABLE_REMOTE_UPLOADS", "true");
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions { FullPage = true });

            Assert.True(Logged("isDisableRemoteUpload"));
        }

        /// <summary>A driver wired to answer begin and screenshot, for tests that only read the request.</summary>
        private static StubMobileDriver CapturingDriver()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ScriptReplies.Add(("\"state\":\"begin\"", BeginResult));
            driver.ScriptReplies.Add(("\"state\":\"screenshot\"", TileResult));
            return driver;
        }
    }
}
