using System.Text.Json;
using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class PercyOptionsTests : CoreTestBase
    {
        private static PercyOptions Build(StubMobileDriver driver) =>
            new PercyOptions(driver, new Cache<string, object?>());

        [Fact]
        public void WithNothingDeclaredPercyIsEnabledAndErrorsAreIgnored()
        {
            // Percy is opt-out: a session that says nothing about it still takes snapshots, and a
            // visual check that cannot run does not fail the functional step.
            PercyOptions options = Build(StubMobileDriver.Android());

            Assert.True(options.PercyEnabled());
            Assert.True(options.IgnoreErrors());
        }

        [Fact]
        public void TheW3cPercyOptionsBagCanTurnPercyOff()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["percyOptions"] = new Dictionary<string, object?> { ["enabled"] = false };

            Assert.False(Build(driver).PercyEnabled());
            Assert.True(Logged("disabled in capabilities"));
        }

        [Fact]
        public void TheJsonWireCapabilityCanTurnPercyOff()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["percy.enabled"] = "false";

            Assert.False(Build(driver).PercyEnabled());
        }

        [Fact]
        public void AnExplicitlyEnabledSessionStaysEnabled()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["percyOptions"] = new Dictionary<string, object?> { ["enabled"] = true };

            Assert.True(Build(driver).PercyEnabled());
        }

        [Fact]
        public void APercyOptionsBagWithoutTheEnabledKeyLeavesPercyEnabled()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["percyOptions"] = new Dictionary<string, object?> { ["ignoreErrors"] = false };

            Assert.True(Build(driver).PercyEnabled());
        }

        [Fact]
        public void ErrorsCanBeMadeToFailTheStepFromEitherProtocol()
        {
            StubMobileDriver w3c = StubMobileDriver.Android();
            w3c.Caps["percyOptions"] = new Dictionary<string, object?> { ["ignoreErrors"] = false };
            Assert.False(Build(w3c).IgnoreErrors());

            StubMobileDriver json = StubMobileDriver.Android();
            json.Caps["percy.ignoreErrors"] = false;
            Assert.False(Build(json).IgnoreErrors());
        }

        [Fact]
        public void AnIgnoreErrorsBagWithoutTheKeyKeepsTheDefault()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["percyOptions"] = new Dictionary<string, object?> { ["enabled"] = true };

            Assert.True(Build(driver).IgnoreErrors());
        }

        [Fact]
        public void ThePercyOptionsBagIsReadOncePerSession()
        {
            Cache<string, object?> cache = new Cache<string, object?>();
            StubMobileDriver driver = StubMobileDriver.Android();
            PercyOptions options = new PercyOptions(driver, cache);

            options.PercyEnabled();

            Assert.True(cache.Has("percyOptions_" + driver.SessionId));
        }
    }

    public class AppPercyTests : CoreTestBase
    {
        private const string Healthy =
            "{\"success\":true,\"build\":{\"id\":\"b\",\"url\":\"https://percy.io/b\"}}";

        private static (AppPercy Percy, StubHttpMessageHandler Handler) Build(
            StubMobileDriver driver, string? comparisonBody = null)
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", Healthy)
                .Default(comparisonBody ?? "{\"success\":true,\"data\":{\"link\":\"https://percy.io/c/1\"}}");
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            return (new AppPercy(driver, client), handler);
        }

        [Fact]
        public void ASnapshotIsCapturedAndPostedAndItsDataReturned()
        {
            (AppPercy percy, StubHttpMessageHandler handler) = Build(StubMobileDriver.Android());

            JsonElement? data = percy.Screenshot("home", new ScreenshotOptions());

            Assert.Equal("https://percy.io/c/1", Json.PropertyAsString(data, "link"));
            Assert.Equal(1, handler.CountFor("/percy/comparison"));
        }

        [Fact]
        public void NothingIsCapturedWhenPercyIsNotRunning()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", "{\"success\":false,\"error\":\"no token\"}");
            AppPercy percy = new AppPercy(StubMobileDriver.Android(),
                new PercyClient(handler.Client(), "http://localhost:5338"));

            Assert.Null(percy.Screenshot("home", new ScreenshotOptions()));
            Assert.Equal(0, handler.CountFor("/percy/comparison"));
        }

        [Fact]
        public void NothingIsCapturedWhenTheSessionDisabledPercy()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["percyOptions"] = new Dictionary<string, object?> { ["enabled"] = false };
            (AppPercy percy, StubHttpMessageHandler handler) = Build(driver);

            Assert.Null(percy.Screenshot("home", new ScreenshotOptions()));
            Assert.Equal(0, handler.CountFor("/percy/comparison"));
        }

        [Fact]
        public void ACaptureFailureIsSwallowedReportedAndNamedByDefault()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.ScreenshotError = new InvalidOperationException("device asleep");
            (AppPercy percy, StubHttpMessageHandler handler) = Build(driver);

            Assert.Null(percy.Screenshot("home", new ScreenshotOptions()));

            // Named on the log line because this method returns null and PostFailedEvent redacts
            // what it forwards — otherwise no full copy of the detail survives anywhere.
            Assert.True(Logged("Error taking screenshot home"));
            Assert.True(Logged("InvalidOperationException"));
            Assert.True(Logged("device asleep"));
            // Percy is told, so the failure shows up in the build and not only in a Tosca log.
            Assert.Equal(1, handler.CountFor("/percy/events"));
        }

        [Fact]
        public void ACaptureFailurePropagatesWhenTheSessionAskedForThat()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["percyOptions"] = new Dictionary<string, object?> { ["ignoreErrors"] = false };
            driver.ScreenshotError = new InvalidOperationException("device asleep");
            (AppPercy percy, _) = Build(driver);

            PercyException error = Assert.Throws<PercyException>(
                () => percy.Screenshot("home", new ScreenshotOptions()));
            Assert.Contains("Error taking screenshot home", error.Message);
            Assert.IsType<InvalidOperationException>(error.InnerException);
        }

        [Fact]
        public void ASessionThatCannotServeARequestIsCalledOutAsSuch()
        {
            // Distinct from a generic failure so the message points at the module parameters that
            // work around it, rather than implying the snapshot was malformed.
            StubMobileDriver driver = new StubMobileDriver { PlatformName = "Windows Phone" };
            (AppPercy percy, _) = Build(driver);

            Assert.Null(percy.Screenshot("home", new ScreenshotOptions()));
            Assert.True(Logged("could not serve this request"));
        }

        [Fact]
        public void CredentialsAreRedactedFromWhatIsReportedToPercy()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.ScreenshotError = new InvalidOperationException(
                "failed against https://user:key@hub.browserstack.com/wd/hub");
            (AppPercy percy, StubHttpMessageHandler handler) = Build(driver);

            percy.Screenshot("home", new ScreenshotOptions());

            string body = handler.BodyFor("/percy/events")!;
            Assert.DoesNotContain("user:key", body);
            Assert.Contains("***@hub.browserstack.com", body);
        }

        [Fact]
        public void TheHealthcheckHappensOncePerSessionNotPerStep()
        {
            (AppPercy percy, StubHttpMessageHandler handler) = Build(StubMobileDriver.Android());

            percy.Screenshot("one", new ScreenshotOptions());
            percy.Screenshot("two", new ScreenshotOptions());

            Assert.Equal(1, handler.CountFor("/percy/healthcheck"));
            Assert.Equal(2, handler.CountFor("/percy/comparison"));
        }

        [Fact]
        public void TheSessionCacheCanBeDropped()
        {
            (AppPercy percy, _) = Build(StubMobileDriver.Android());
            percy.Screenshot("one", new ScreenshotOptions());

            percy.ClearSessionCache();

            // Still usable afterwards; the cache is a memo, not state the snapshot depends on.
            Assert.NotNull(percy.Screenshot("two", new ScreenshotOptions()));
        }
    }

    public class PercyOnAutomateTests : CoreTestBase
    {
        private const string Healthy =
            "{\"success\":true,\"type\":\"automate\",\"build\":{\"id\":\"b\",\"url\":\"u\"}}";

        private static (PercyOnAutomate Percy, StubHttpMessageHandler Handler) Build(
            StubMobileDriver driver, string? screenshotBody = null)
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", Healthy)
                .Default(screenshotBody ?? "{\"success\":true,\"data\":{\"link\":\"https://percy.io/c/2\"}}");
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            return (new PercyOnAutomate(driver, client), handler);
        }

        private static StubMobileDriver AutomateDriver()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = "https://hub-cloud.browserstack.com/wd/hub/";
            return driver;
        }

        [Fact]
        public void TheSessionDetailsAreHandedToTheCliRatherThanAnyImageData()
        {
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(AutomateDriver());

            JsonElement? data = percy.Screenshot("cart",
                new Dictionary<string, object?> { ["full_screen"] = true });

            Assert.Equal("https://percy.io/c/2", Json.PropertyAsString(data, "link"));

            string body = handler.BodyFor("/percy/automateScreenshot")!;
            Assert.Contains("\"sessionId\":\"session-1\"", body);
            // The trailing slash is trimmed: the CLI appends its own paths to this.
            Assert.Contains("\"commandExecutorUrl\":\"https://hub-cloud.browserstack.com/wd/hub\"", body);
            Assert.Contains("\"platformName\":\"Android\"", body);
            Assert.Contains("\"full_screen\":true", body);
        }

        [Fact]
        public void ASnapshotWithNoOptionsIsStillPosted()
        {
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(AutomateDriver());

            percy.Screenshot("cart");

            Assert.Contains("\"options\":{}", handler.BodyFor("/percy/automateScreenshot")!);
        }

        [Fact]
        public void NothingIsPostedWhenPercyIsNotRunning()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", "{\"success\":false,\"error\":\"no token\"}");
            PercyOnAutomate percy = new PercyOnAutomate(AutomateDriver(),
                new PercyClient(handler.Client(), "http://localhost:5338"));

            Assert.Null(percy.Screenshot("cart"));
            Assert.Equal(0, handler.CountFor("/percy/automateScreenshot"));
        }

        [Fact]
        public void ElementLocatorsAreReplacedWithTheSessionsOwnElementIds()
        {
            // The CLI re-resolves the elements server-side rather than trusting coordinates we
            // computed, so it needs ids and not rectangles.
            StubMobileDriver driver = AutomateDriver();
            driver.ElementsByXPath["//total"] = new ElementRect(0, 0, 10, 10, "el-1");
            driver.ElementsByXPath["//banner"] = new ElementRect(0, 0, 10, 10, "el-2");
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(driver);

            percy.Screenshot("cart", new Dictionary<string, object?>
            {
                [PercyOnAutomate.IgnoreElementKey] = new List<object?> { "//total" },
                [PercyOnAutomate.ConsiderElementKey] = new List<object?> { "//banner" }
            });

            string body = handler.BodyFor("/percy/automateScreenshot")!;
            Assert.Contains("\"ignore_region_elements\":[\"el-1\"]", body);
            Assert.Contains("\"consider_region_elements\":[\"el-2\"]", body);
            // The input keys are consumed, not forwarded alongside.
            Assert.DoesNotContain(PercyOnAutomate.IgnoreElementKey, body);
            Assert.DoesNotContain(PercyOnAutomate.ConsiderElementKey, body);
        }

        [Fact]
        public void ALocatorThatMatchesNothingIsDroppedWithALogLine()
        {
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(AutomateDriver());

            percy.Screenshot("cart", new Dictionary<string, object?>
            {
                [PercyOnAutomate.IgnoreElementKey] = new List<object?> { "//missing" }
            });

            Assert.True(Logged("//missing"));
            Assert.Contains("\"ignore_region_elements\":[]", handler.BodyFor("/percy/automateScreenshot")!);
        }

        [Fact]
        public void AnElementWithoutAnIdIsDropped()
        {
            // A rect with no id is useless here: the CLI can only act on ids.
            StubMobileDriver driver = AutomateDriver();
            driver.ElementsByXPath["//total"] = new ElementRect(0, 0, 10, 10);
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(driver);

            percy.Screenshot("cart", new Dictionary<string, object?>
            {
                [PercyOnAutomate.IgnoreElementKey] = new List<object?> { "//total" }
            });

            Assert.Contains("\"ignore_region_elements\":[]", handler.BodyFor("/percy/automateScreenshot")!);
        }

        [Fact]
        public void ALocatorLookupThatThrowsIsDropped()
        {
            StubMobileDriver driver = AutomateDriver();
            driver.FindElementError = new InvalidOperationException("stale");
            (PercyOnAutomate percy, _) = Build(driver);

            percy.Screenshot("cart", new Dictionary<string, object?>
            {
                [PercyOnAutomate.IgnoreElementKey] = new List<object?> { "//x" }
            });

            Assert.True(Logged("//x"));
        }

        [Fact]
        public void BlankAndNonStringLocatorsAreIgnored()
        {
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(AutomateDriver());

            percy.Screenshot("cart", new Dictionary<string, object?>
            {
                [PercyOnAutomate.IgnoreElementKey] = new List<object?> { "", "  ", null, 42 }
            });

            Assert.Contains("\"ignore_region_elements\":[]", handler.BodyFor("/percy/automateScreenshot")!);
        }

        [Fact]
        public void AnElementKeyThatIsNotAListIsDroppedRatherThanForwarded()
        {
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(AutomateDriver());

            percy.Screenshot("cart", new Dictionary<string, object?>
            {
                [PercyOnAutomate.IgnoreElementKey] = "//not-a-list"
            });

            string body = handler.BodyFor("/percy/automateScreenshot")!;
            // A bare string is iterable as chars; forwarding it would send one "element id" per
            // character.
            Assert.DoesNotContain("ignore_region_elements", body);
            Assert.DoesNotContain(PercyOnAutomate.IgnoreElementKey, body);
        }

        [Fact]
        public void TheCallersOptionDictionaryIsNotMutated()
        {
            // Tosca reuses a module's parameter set across rows; consuming keys out of the caller's
            // dictionary would make the second row behave differently from the first.
            StubMobileDriver driver = AutomateDriver();
            driver.ElementsByXPath["//total"] = new ElementRect(0, 0, 1, 1, "el-1");
            (PercyOnAutomate percy, _) = Build(driver);

            Dictionary<string, object?> options = new Dictionary<string, object?>
            {
                [PercyOnAutomate.IgnoreElementKey] = new List<object?> { "//total" }
            };

            percy.Screenshot("cart", options);

            Assert.True(options.ContainsKey(PercyOnAutomate.IgnoreElementKey));
        }

        [Fact]
        public void ARefusedSnapshotReturnsNullRatherThanThrowing()
        {
            (PercyOnAutomate percy, _) = Build(AutomateDriver(),
                "{\"success\":false,\"error\":\"bad session\"}");

            Assert.Null(percy.Screenshot("cart"));
        }

        [Fact]
        public void ASessionThatThrowsWhileBeingReadIsSwallowedAndLogged()
        {
            // Nothing about a Percy on Automate snapshot should fail the functional step, including
            // a session that cannot answer basic questions about itself.
            StubMobileDriver driver = AutomateDriver();
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(driver);
            driver.SessionIdError = new InvalidOperationException("session closed");
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Null(percy.Screenshot("cart"));
            Assert.True(Logged("Could not take Percy Screenshot \"cart\""));
            Assert.Equal(0, handler.CountFor("/percy/automateScreenshot"));
        }

        [Fact]
        public void ASessionWithNoHostStillPostsWithoutOne()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Host = null;
            (PercyOnAutomate percy, StubHttpMessageHandler handler) = Build(driver);

            percy.Screenshot("cart");

            Assert.Contains("\"commandExecutorUrl\":null", handler.BodyFor("/percy/automateScreenshot")!);
        }
    }
}
