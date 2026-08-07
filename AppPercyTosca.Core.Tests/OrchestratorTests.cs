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

        /// <summary>
        /// These cover AppPercy's orchestration — healthcheck, enablement, error handling — not the
        /// executor. Remote uploads are switched off so capture takes the local-tile path and the
        /// driver does not need to answer browserstack_executor commands to make the point.
        /// </summary>
        private (AppPercy Percy, StubHttpMessageHandler Handler) Build(
            StubMobileDriver driver, string? comparisonBody = null)
        {
            SetEnv("PERCY_DISABLE_REMOTE_UPLOADS", "true");
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", Healthy)
                .Default(comparisonBody ?? "{\"success\":true,\"link\":\"https://percy.io/c/1\",\"data\":{\"id\":\"c1\"}}");
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            return (new AppPercy(driver, client), handler);
        }

        [Fact]
        public void ASnapshotIsCapturedAndPostedAndTheWholeResponseReturned()
        {
            (AppPercy percy, StubHttpMessageHandler handler) = Build(StubMobileDriver.Android());

            JsonElement? response = percy.Screenshot("home", new ScreenshotOptions());

            // The whole response, not its `data` member: a successful reply often carries no `data`, so
            // its presence cannot be what tells success from failure.
            Assert.Equal("https://percy.io/c/1", Json.PropertyAsString(response, "link"));
            Assert.Equal(1, handler.CountFor("/percy/comparison"));
        }

        [Fact]
        public void ASuccessfulReplyWithNoDataMemberIsStillASnapshot()
        {
            // The shape the CLI actually returns, and the shape the reference SDK's own fixture uses.
            // Reading success from a `data` member reported working snapshots as unrecorded.
            (AppPercy percy, _) = Build(StubMobileDriver.Android(),
                "{\"success\":true,\"link\":\"https://percy.io/c/9\"}");

            JsonElement? response = percy.Screenshot("home", new ScreenshotOptions());

            Assert.NotNull(response);
            Assert.Equal(SnapshotOutcome.Taken + " https://percy.io/c/9",
                SnapshotOutcome.Describe(response, "home"));
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
}
