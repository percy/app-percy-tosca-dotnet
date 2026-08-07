using System.Text.Json;
using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class AppAutomateReadinessTests : CoreTestBase
    {
        [Fact]
        public void ABrowserStackHostIsRecognisedAsAppAutomate()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "https://hub-cloud.browserstack.com/wd/hub";

            Assert.True(AppAutomate.Supports(driver));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("http://127.0.0.1:4723/wd/hub")]
        public void AnyOtherHostIsNot(string? host)
        {
            // No longer selects a provider — there is only one — but a session that is not App Automate
            // should be called out rather than failing on its first executor command.
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

    /// <summary>
    /// The parts of the provider that are not the executor: the device tag, region resolution, and the
    /// locally-written tile used when remote uploads are off.
    /// </summary>
    public class AppAutomateLocalCaptureTests : CoreTestBase
    {
        private const string Accepted =
            "{\"success\":true,\"link\":\"https://percy.io/c/1\",\"data\":{\"id\":\"c1\"}}";

        private static (AppAutomate Provider, StubHttpMessageHandler Handler) Build(StubMobileDriver driver)
        {
            // Remote uploads off: the hub is not what these are about, and the local tile is the only
            // path that does not need an executor to answer.
            Environment.SetEnvironmentVariable("PERCY_DISABLE_REMOTE_UPLOADS", "true");
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default(Accepted);
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            return (new AppAutomate(driver, client, new Cache<string, object?>()), handler);
        }

        private static StubMobileDriver AutomateDriver()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "https://hub-cloud.browserstack.com/wd/hub";
            return driver;
        }

        [Fact]
        public void ASnapshotPostsATileWrittenToDiskAlongsideTheDeviceTag()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "percy-tests-" + Guid.NewGuid());
            SetEnv("PERCY_TMP_DIR", tempDir);
            try
            {
                (AppAutomate provider, StubHttpMessageHandler handler) = Build(AutomateDriver());

                Assert.NotNull(provider.Screenshot("home", new ScreenshotOptions()));

                string body = handler.BodyFor("/percy/comparison")!;
                Assert.Contains("\"name\":\"home\"", body);
                Assert.Contains("\"osName\":\"Android\"", body);
                Assert.Contains("\"statusBarHeight\":60", body);
                Assert.Contains("\"navBarHeight\":40", body);

                // The CLI reads the tile from disk, so the file must be there and hold decoded bytes.
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
        public void AnEmptyScreenshotIsReportedAsSomethingActionable()
        {
            // Convert.FromBase64String("") succeeds and would write a 0-byte PNG the CLI then rejects
            // with a much less useful message.
            StubMobileDriver driver = AutomateDriver();
            driver.Screenshot = "";
            (AppAutomate provider, _) = Build(driver);

            PercyException error = Assert.Throws<PercyException>(
                () => provider.Screenshot("home", new ScreenshotOptions()));
            Assert.Contains("empty screenshot", error.Message);
        }

        [Fact]
        public void RegionsResolveToDevicePixelCoordinates()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.ElementsByXPath["//total"] = new ElementRect(10, 20, 100, 50);
            driver.ElementsByAccessibilityId["banner"] = new ElementRect(0, 0, 200, 30);
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

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
        public void ALocatorThatMatchesNothingIsSkippedRatherThanFailingTheSnapshot()
        {
            // A sheet that declares one ignore region and reuses it will legitimately hit screens where
            // the element is absent.
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(AutomateDriver());

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
            StubMobileDriver driver = AutomateDriver();
            driver.FindElementError = new InvalidOperationException("stale element");
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                IgnoreRegionXpaths = new List<string> { "//x" }
            });

            Assert.True(Logged("//x"));
        }

        [Fact]
        public void CustomRegionsArePassedThroughUnscaled()
        {
            // Declared in device pixels already, so scaling would double-apply.
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(AutomateDriver());

            provider.Screenshot("home", new ScreenshotOptions
            {
                CustomIgnoreRegions = new List<Region> { new Region(0, 100, 0, 200) }
            });

            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains("\"selector\":\"custom region 0\"", body);
            Assert.Contains("\"top\":0,\"bottom\":100,\"left\":0,\"right\":200", body);
        }

        [Fact]
        public void ACustomRegionOutsideTheScreenIsReportedAndSkipped()
        {
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(AutomateDriver());

            provider.Screenshot("home", new ScreenshotOptions
            {
                CustomConsiderRegions = new List<Region> { new Region(0, 99999, 0, 200) }
            });

            Assert.True(Logged("is not valid"));
            Assert.Contains("\"considerElementsData\":[]", handler.BodyFor("/percy/comparison")!);
        }

        [Fact]
        public void CustomRegionsSurviveAnUnknownScreenSizeInsteadOfBeingDiscarded()
        {
            // Validating against a 0x0 screen rejects every region, so the user loses the only region
            // type that needs no element lookup — and the message blames the region rather than the
            // missing dimensions.
            StubMobileDriver driver = AutomateDriver();
            driver.Caps.Remove("deviceScreenSize");
            driver.Caps.Remove("viewportRect");
            (AppAutomate provider, StubHttpMessageHandler handler) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                CustomIgnoreRegions = new List<Region> { new Region(0, 100, 0, 200) }
            });

            Assert.Contains("\"top\":0,\"bottom\":100,\"left\":0,\"right\":200",
                handler.BodyFor("/percy/comparison")!);
            Assert.False(Logged("is not valid"));
        }

        [Fact]
        public void AnUnknownScreenSizeIsReportedBecauseItCorruptsTheComparisonTag()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.Caps.Remove("deviceScreenSize");
            driver.Caps.Remove("viewportRect");
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            // Reports the tag it actually sent and what to check, rather than naming parameters
            // that were removed once device details became session-only.
            Assert.True(Logged("tagged 0x0"));
            Assert.True(Logged("read from the device session"));
        }

        [Fact]
        public void ASessionThatIsNotAppAutomateIsCalledOut()
        {
            // With one provider there is nothing to fall back to, so say so rather than letting the
            // executor commands fail with something less obvious.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "http://127.0.0.1:4723/wd/hub";
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions());

            Assert.True(Logged("does not look like BrowserStack App Automate"));
        }

        [Fact]
        public void FullPageIsAnnouncedAsIncompatibleWithDisabledRemoteUploads()
        {
            (AppAutomate provider, _) = Build(AutomateDriver());

            provider.Screenshot("home", new ScreenshotOptions { FullPage = true });

            Assert.True(Logged("isDisableRemoteUpload"));
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

            provider.Screenshot("home", new ScreenshotOptions());

            string end = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"end\""));
            Assert.Contains("\"status\":\"success\"", end);
            Assert.Contains("\"percyScreenshotUrl\":\"https://percy.io/c/1\"", end);
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

            Assert.Null(provider.ExecutePercyScreenshotEnd("home", null, null));
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
        public void TheScreenshotRequestCarriesTheFullPageOptions()
        {
            StubMobileDriver driver = CapturingDriver();
            (AppAutomate provider, _) = Build(driver);

            provider.Screenshot("home", new ScreenshotOptions
            {
                FullPage = true,
                ScreenLengths = 3,
                IosOptimizedFullpage = true
            });

            string request = driver.ExecutedScripts.First(s => s.Contains("\"state\":\"screenshot\""));
            Assert.Contains("\"numOfTiles\":3", request);
            Assert.Contains("\"iosOptimizedFullpage\":true", request);
            // The hub chooses the scrollable view and the offsets itself.
            Assert.DoesNotContain("scollableXpath", request);
            Assert.DoesNotContain("ScrollviewOffset", request);
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
