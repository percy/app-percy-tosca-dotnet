using System.Net;
using System.Text.Json;
using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class PercyClientTests : CoreTestBase
    {
        private const string HealthyBody =
            "{\"success\":true,\"build\":{\"id\":\"build-1\",\"url\":\"https://percy.io/b/1\"}}";

        private static PercyClient Client(StubHttpMessageHandler handler, string? api = null) =>
            new PercyClient(handler.Client(), api ?? "http://localhost:5338");

        [Fact]
        public void ARequestWithNoPayloadIsAGet()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default("{\"ok\":1}");

            PercyResponse response = Client(handler).Request("/percy/dom.js");

            Assert.Equal("GET", handler.Requests[0].Method);
            Assert.Equal("http://localhost:5338/percy/dom.js", handler.Requests[0].Url);
            Assert.Equal("{\"ok\":1}", response.Content);
            Assert.Equal("1.27.0", response.Version);
        }

        [Fact]
        public void ARequestWithAPayloadIsAPostCarryingJson()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default("{}");

            Client(handler).Request("/percy/log", new Dictionary<string, object?> { ["message"] = "hi" });

            Assert.Equal("POST", handler.Requests[0].Method);
            Assert.Equal("{\"message\":\"hi\"}", handler.Requests[0].Body);
        }

        [Fact]
        public void AnAlreadyJsonPayloadIsSentVerbatim()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default("{}");

            Client(handler).Request("/percy/log", "{\"raw\":true}", isJson: true);

            Assert.Equal("{\"raw\":true}", handler.Requests[0].Body);
        }

        [Fact]
        public void ATrailingSlashOnTheCliApiIsNotDoubledIntoTheUrl()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default("{}");

            Client(handler, "http://localhost:5338/").Request("/percy/healthcheck");

            Assert.Equal("http://localhost:5338/percy/healthcheck", handler.Requests[0].Url);
        }

        [Fact]
        public void TheCliApiEnvironmentVariableIsUsedWhenNoneIsPassed()
        {
            SetEnv("PERCY_CLI_API", "http://elsewhere:9999");
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default("{}");

            new PercyClient(handler.Client()).Request("/percy/healthcheck");

            Assert.Equal("http://elsewhere:9999/percy/healthcheck", handler.Requests[0].Url);
        }

        [Fact]
        public void AMissingVersionHeaderIsReportedAsNoVersion()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default("{}", coreVersion: null);

            Assert.Null(Client(handler).Request("/percy/healthcheck").Version);
        }

        [Fact]
        public void AFailingStatusCodeThrows()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("nope", HttpStatusCode.InternalServerError);

            Assert.ThrowsAny<Exception>(() => Client(handler).Request("/percy/healthcheck"));
        }

        [Fact]
        public void AHealthyCliRecordsTheBuildAndSessionType()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck",
                    "{\"success\":true,\"type\":\"automate\"," +
                    "\"build\":{\"id\":\"build-1\",\"url\":\"https://percy.io/b/1\"}}");

            Assert.True(Client(handler).Healthcheck());
            Assert.Equal("build-1", Env.PercyBuildId);
            Assert.Equal("https://percy.io/b/1", Env.PercyBuildUrl);
            Assert.Equal("automate", Env.SessionType);
            Assert.True(Env.IsAutomateSession);
        }

        [Fact]
        public void AnUnquotedBuildIdIsStillRecorded()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", "{\"success\":true,\"build\":{\"id\":42}}");

            Assert.True(Client(handler).Healthcheck());
            Assert.Equal("42", Env.PercyBuildId);
        }

        [Fact]
        public void TheHealthcheckIsMemoizedForTheProcess()
        {
            // A Tosca sheet can hold dozens of PercyScreenshot steps; re-checking on each would
            // add a round trip per step for an answer that cannot change.
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default(HealthyBody);
            PercyClient client = Client(handler);

            Assert.True(client.Healthcheck());
            Assert.True(client.Healthcheck());

            Assert.Equal(1, handler.CountFor("/percy/healthcheck"));
        }

        [Fact]
        public void ACliThatReportsFailureDisablesSnapshots()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", "{\"success\":false,\"error\":\"token missing\"}");

            Assert.False(Client(handler).Healthcheck());
            Assert.True(Logged("Percy is not running"));
            Assert.True(Logged("token missing"));
        }

        [Fact]
        public void AFailureWithNoErrorMessageStillReportsSomething()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", "{\"success\":false}");

            Assert.False(Client(handler).Healthcheck());
            Assert.True(Logged("Percy healthcheck failed"));
        }

        [Fact]
        public void AnUnreachableCliDisablesSnapshotsRatherThanThrowing()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("gone", HttpStatusCode.ServiceUnavailable);

            Assert.False(Client(handler).Healthcheck());
            Assert.True(Logged("Percy is not running"));
        }

        [Fact]
        public void APercyAgentSessionIsRefusedWithMigrationInstructions()
        {
            // @percy/agent answers the healthcheck but sends no version header.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/healthcheck", HealthyBody, coreVersion: null);

            Assert.False(Client(handler).Healthcheck());
            Assert.True(Logged("@percy/agent"));
            Assert.True(Logged("migrate-to-cli"));
        }

        [Theory]
        [InlineData("1.27.0", true)]
        [InlineData("1.27.0-beta.1", true)]
        [InlineData("1.28.4", true)]
        [InlineData("2.0.0", true)]      // newer major: the minor is irrelevant
        [InlineData("10.1.0", true)]
        [InlineData("1.26.9", false)]
        [InlineData("1.0.0", false)]
        [InlineData("1", false)]          // no minor, so the gate cannot be shown to be met
        [InlineData("0.9.0", false)]
        [InlineData("x.y.z", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TheVersionGateRequiresTheCliThatServesAppPercy(string? version, bool expected)
        {
            Assert.Equal(expected, PercyClient.VersionSupported(version));
        }

        [Fact]
        public void AnOldCliNamesTheVersionItNeeds()
        {
            PercyClient.VersionSupported("1.20.0");
            Assert.True(Logged("1.27.0"));
        }

        [Fact]
        public void AnUnparseableVersionSaysSoRatherThanBeingAssumedGood()
        {
            // Assuming good would post to a CLI with no /percy/comparison and fail every snapshot
            // with a much less obvious error.
            PercyClient.VersionSupported("x.y.z");
            Assert.True(Logged("Could not parse"));

            PercyClient.VersionSupported(null);
            Assert.True(Logged("Could not determine"));
        }

        [Fact]
        public void AMajorBelowOneIsCalledUnsupported()
        {
            PercyClient.VersionSupported("0.9.0");
            Assert.True(Logged("Unsupported Percy CLI version"));
        }

        [Fact]
        public void PostScreenshotSendsTheTagTilesAndRegionsAndReturnsTheDataObject()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/comparison",
                    "{\"success\":true,\"data\":{\"link\":\"https://percy.io/c/1\"}}");

            JsonElement? data = Client(handler).PostScreenshot(
                "home",
                new Dictionary<string, object?> { ["name"] = "Pixel 7" },
                new List<Tile> { new Tile("/tmp/a.png", 60, 40, 0, 0, false) },
                "https://app-automate.browserstack.com/x",
                new Dictionary<string, object?> { ["ignoreElementsData"] = new List<object>() },
                new Dictionary<string, object?> { ["considerElementsData"] = new List<object>() },
                new ScreenshotOptions { Sync = true, TestCase = "tc", Labels = "l" });

            Assert.Equal("https://percy.io/c/1", Json.PropertyAsString(data, "link"));

            string body = handler.BodyFor("/percy/comparison")!;
            Assert.Contains($"\"clientInfo\":\"{Env.ClientInfo}\"", body);
            Assert.Contains("\"environmentInfo\":\"tosca\"", body);
            Assert.Contains("\"name\":\"home\"", body);
            Assert.Contains("\"filepath\":\"/tmp/a.png\"", body);
            Assert.Contains("\"externalDebugUrl\":\"https://app-automate.browserstack.com/x\"", body);
            Assert.Contains("\"sync\":true", body);
            Assert.Contains("\"testCase\":\"tc\"", body);
        }

        [Fact]
        public void PostScreenshotReturnsNullAndLogsWhenTheCliRefusesIt()
        {
            // A visual snapshot must not fail an otherwise-passing Tosca step, but the CLI's reason
            // is the only account of why nothing appeared in the build, so it has to be logged.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/comparison", "{\"success\":false,\"error\":\"tile missing\"}");

            Assert.Null(Client(handler).PostScreenshot(
                "home", new Dictionary<string, object?>(), new List<Tile>(),
                null, null, null, new ScreenshotOptions()));

            Assert.True(Logged("Could not take screenshot \"home\""));
        }

        [Fact]
        public void PostScreenshotReportsARefusalThatCarriesNoMessage()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/comparison", "{\"success\":false}");
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Null(Client(handler).PostScreenshot(
                "home", new Dictionary<string, object?>(), new List<Tile>(),
                null, null, null, new ScreenshotOptions()));

            Assert.True(Logged("rejected screenshot \"home\""));
        }

        [Fact]
        public void PostScreenshotSurvivesATransportFailure()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("boom", HttpStatusCode.InternalServerError);

            Assert.Null(Client(handler).PostScreenshot(
                "home", new Dictionary<string, object?>(), new List<Tile>(),
                null, null, null, new ScreenshotOptions()));
            Assert.True(Logged("Could not take screenshot"));
        }

        [Fact]
        public void PostAutomateScreenshotSendsTheSessionDetailsRatherThanImageData()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/automateScreenshot",
                    "{\"success\":true,\"data\":{\"link\":\"https://percy.io/c/2\"}}");

            JsonElement? data = Client(handler).PostAutomateScreenshot(
                "cart",
                "session-9",
                "https://hub.browserstack.com/wd/hub",
                new Dictionary<string, object?> { ["platformName"] = "Android" },
                new Dictionary<string, object?> { ["full_screen"] = true });

            Assert.Equal("https://percy.io/c/2", Json.PropertyAsString(data, "link"));

            string body = handler.BodyFor("/percy/automateScreenshot")!;
            Assert.Contains("\"sessionId\":\"session-9\"", body);
            Assert.Contains("\"commandExecutorUrl\":\"https://hub.browserstack.com/wd/hub\"", body);
            Assert.Contains("\"snapshotName\":\"cart\"", body);
            Assert.Contains("\"full_screen\":true", body);
        }

        [Fact]
        public void PostAutomateScreenshotReturnsNullAndLogsWhenRefused()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/automateScreenshot", "{\"success\":false,\"error\":\"bad session\"}");

            Assert.Null(Client(handler).PostAutomateScreenshot(
                "cart", "s", "h", new Dictionary<string, object?>(),
                new Dictionary<string, object?>()));

            Assert.True(Logged("Could not take screenshot \"cart\""));
        }

        [Fact]
        public void FailedEventsAreForwardedToPercy()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/events", "{\"success\":true}");

            Client(handler).PostFailedEvent("something broke");

            string body = handler.BodyFor("/percy/events")!;
            Assert.Contains("\"message\":\"something broke\"", body);
            Assert.Contains("\"errorKind\":\"sdk\"", body);
        }

        [Fact]
        public void AFailedEventThatCannotBeSentIsSwallowed()
        {
            // Best-effort by design: failing here would replace a reported error with a new one.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/percy/events", "{\"success\":false,\"error\":\"nope\"}");
            SetEnv("PERCY_LOGLEVEL", "debug");

            Client(handler).PostFailedEvent("something broke");

            Assert.True(Logged("Could not send failed event"));
        }

        [Fact]
        public void AFailedEventSurvivesATransportFailure()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("boom", HttpStatusCode.BadGateway);
            SetEnv("PERCY_LOGLEVEL", "debug");

            Client(handler).PostFailedEvent("something broke");

            Assert.True(Logged("Could not send failed event"));
        }

        [Fact]
        public void LogLinesAreForwardedToTheCli()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler().Default("{}");

            Client(handler).PostLog("hello", "warn");

            string body = handler.BodyFor("/percy/log")!;
            Assert.Contains("\"message\":\"hello\"", body);
            Assert.Contains("\"level\":\"warn\"", body);
        }
    }
}
