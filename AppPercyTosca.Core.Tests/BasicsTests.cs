using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class EnvTests : CoreTestBase
    {
        [Fact]
        public void ClientInfoNamesThisSdkAndItsVersion()
        {
            // Percy attributes builds by this string, so the prefix is part of the contract.
            Assert.Equal($"percy-app-tosca/{Env.SdkVersion}", Env.ClientInfo);
        }

        [Fact]
        public void EnvironmentInfoCarriesTheToscaVersionWhenTheShimSuppliedOne()
        {
            Assert.Equal("tosca", Env.EnvironmentInfo);

            Env.ToscaVersion = "24.0";
            Assert.Equal("tosca/24.0", Env.EnvironmentInfo);

            Env.ToscaVersion = "   ";
            Assert.Equal("tosca", Env.EnvironmentInfo);
        }

        [Theory]
        [InlineData("automate", true)]
        [InlineData("AUTOMATE", true)]
        [InlineData("web", false)]
        [InlineData(null, false)]
        public void AnAutomateSessionTypeIsRecognisedSoItCanBeReportedAsUnsupported(
            string? type, bool expected)
        {
            Env.SessionType = type;
            Assert.Equal(expected, Env.IsAutomateSession);
        }

        [Theory]
        [InlineData("FORCE_FULL_PAGE")]
        [InlineData("PERCY_DISABLE_REMOTE_UPLOADS")]
        [InlineData("PERCY_ENABLE_DEV")]
        public void FlagEnvVarsAreReadCaseInsensitivelyAndOnlyForTrue(string name)
        {
            Func<bool> read = name switch
            {
                "FORCE_FULL_PAGE" => Env.ForceFullPage,
                "PERCY_DISABLE_REMOTE_UPLOADS" => Env.DisableRemoteUploads,
                _ => Env.EnablePercyDev
            };

            Assert.False(read());
            SetEnv(name, "TRUE");
            Assert.True(read());
            SetEnv(name, "1");
            Assert.False(read());
        }

        [Fact]
        public void DebugFollowsPercyLogLevel()
        {
            Assert.False(Env.Debug());
            SetEnv("PERCY_LOGLEVEL", "debug");
            Assert.True(Env.Debug());
            SetEnv("PERCY_LOGLEVEL", "info");
            Assert.False(Env.Debug());
        }

        [Fact]
        public void CliApiDefaultsToTheLocalCliPort()
        {
            Assert.Equal("http://localhost:5338", Env.CliApi());
            SetEnv("PERCY_CLI_API", "http://elsewhere:1234");
            Assert.Equal("http://elsewhere:1234", Env.CliApi());
        }

        [Fact]
        public void TempDirFallsBackToTheSystemTempPath()
        {
            Assert.Equal(Path.GetTempPath(), Env.TempDir());
            SetEnv("PERCY_TMP_DIR", "/tmp/percy-custom");
            Assert.Equal("/tmp/percy-custom", Env.TempDir());
            SetEnv("PERCY_TMP_DIR", "   ");
            Assert.Equal(Path.GetTempPath(), Env.TempDir());
        }

        [Fact]
        public void AutomateDomainIsOverridable()
        {
            Assert.Equal("browserstack", Env.AutomateDomain());
            SetEnv("AA_DOMAIN", "my-hub.internal");
            Assert.Equal("my-hub.internal", Env.AutomateDomain());
        }

        [Fact]
        public void ResetClearsThePerRunState()
        {
            Env.PercyBuildId = "b";
            Env.PercyBuildUrl = "u";
            Env.SessionType = "automate";
            Env.ToscaVersion = "24";

            Env.Reset();

            Assert.Null(Env.PercyBuildId);
            Assert.Null(Env.PercyBuildUrl);
            Assert.Null(Env.SessionType);
            Assert.Null(Env.ToscaVersion);
        }
    }

    public class UtilsTests : CoreTestBase
    {
        [Theory]
        [InlineData("https://user:key@hub-cloud.browserstack.com/wd/hub",
            "https://***@hub-cloud.browserstack.com/wd/hub")]
        [InlineData("wss://tok@example.com/x", "wss://***@example.com/x")]
        [InlineData("http://u:p@h/x", "http://***@h/x")]
        public void CredentialsInACommandExecutorUrlAreRedacted(string input, string expected)
        {
            Assert.Equal(expected, Utils.RedactCredentials(input));
        }

        [Fact]
        public void ALocatorThatLooksLikeAUrlIsLeftAlone()
        {
            // Region logging emits locators carrying `://` and `@`; keying redaction on the scheme
            // is what keeps those intact.
            const string locator = "xpath://a[@id='x']";
            Assert.Equal(locator, Utils.RedactCredentials(locator));
        }

        [Theory]
        [InlineData("?access_key=abc123", "?access_key=***")]
        [InlineData("&authToken=abc123", "&authToken=***")]
        [InlineData("?password=hunter2&x=1", "?password=***&x=1")]
        [InlineData("?secret=s", "?secret=***")]
        public void CredentialsInQueryStringsAreRedacted(string input, string expected)
        {
            Assert.Equal(expected, Utils.RedactCredentials(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void RedactingNothingYieldsAnEmptyString(string? input)
        {
            Assert.Equal("", Utils.RedactCredentials(input));
        }

        [Fact]
        public void DebugLinesAreDroppedUnlessAskedFor()
        {
            Utils.Log("quiet", "debug");
            Assert.Empty(Logs);

            SetEnv("PERCY_LOGLEVEL", "debug");
            Utils.Log("loud", "debug");
            Assert.Contains(("loud", "debug"), Logs);
        }

        [Fact]
        public void EveryOtherLevelIsAlwaysEmitted()
        {
            Utils.Log("info line");
            Utils.Log("warn line", "warn");

            Assert.Contains(("info line", "info"), Logs);
            Assert.Contains(("warn line", "warn"), Logs);
        }

        [Fact]
        public void MessagesAreRedactedBeforeTheyReachTheSink()
        {
            Utils.Log("failed against https://u:k@hub.browserstack.com/wd/hub");
            Assert.DoesNotContain("u:k", Logs[0].Message);
            Assert.Contains("***@hub.browserstack.com", Logs[0].Message);
        }

        [Fact]
        public void AFailingSinkFallsBackToStdoutRatherThanLosingTheLine()
        {
            // The sink forwards to the Percy CLI, which can be down at exactly the moment we most
            // need the line — so a throwing sink must not swallow it or fail the snapshot.
            Utils.LogSink = (_, _) => throw new InvalidOperationException("CLI unreachable");

            TextWriter original = Console.Out;
            StringWriter captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                Utils.Log("still visible");
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Contains("still visible", captured.ToString());
        }

        [Fact]
        public void WithNoSinkInstalledLinesGoToStdoutLabelledByLevel()
        {
            Utils.LogSink = null;
            SetEnv("PERCY_LOGLEVEL", "debug");

            TextWriter original = Console.Out;
            StringWriter captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                Utils.Log("info line");
                Utils.Log("warn line", "warn");
                Utils.Log("debug line", "debug");
            }
            finally
            {
                Console.SetOut(original);
            }

            string output = captured.ToString();
            Assert.Contains("percy[39m] info line", output);
            Assert.Contains("percy:tosca[93m] warn line", output);
            Assert.Contains("percy:tosca[91m] debug line", output);
        }
    }

    public class CacheTests
    {
        [Fact]
        public void StoredValuesAreReadableAndRemovable()
        {
            Cache<string, object?> cache = new Cache<string, object?>();

            Assert.False(cache.Has("k"));
            Assert.Null(cache.Get("k"));

            cache.Store("k", 42);
            Assert.True(cache.Has("k"));
            Assert.Equal(42, cache.Get("k"));

            cache.Remove("k");
            Assert.False(cache.Has("k"));
        }

        [Fact]
        public void HasDistinguishesACachedNullFromAMiss()
        {
            // The metadata layer caches a failed viewport read as null so it is attempted once per
            // session rather than once per snapshot; that only works if Has() reports it.
            Cache<string, object?> cache = new Cache<string, object?>();
            cache.Store("k", null);

            Assert.True(cache.Has("k"));
            Assert.Null(cache.Get("k"));
        }

        [Fact]
        public void StoringTwiceReplacesTheValue()
        {
            Cache<string, object?> cache = new Cache<string, object?>();
            cache.Store("k", 1);
            cache.Store("k", 2);
            Assert.Equal(2, cache.Get("k"));
        }

        [Fact]
        public void ClearDropsEverything()
        {
            Cache<string, object?> cache = new Cache<string, object?>();
            cache.Store("a", 1);
            cache.Store("b", 2);

            cache.Clear();

            Assert.False(cache.Has("a"));
            Assert.False(cache.Has("b"));
        }
    }

    public class RegionTests
    {
        [Fact]
        public void BoundsAreReadableAndSettable()
        {
            Region region = new Region(1, 2, 3, 4);
            Assert.Equal(1, region.Top);
            Assert.Equal(2, region.Bottom);
            Assert.Equal(3, region.Left);
            Assert.Equal(4, region.Right);

            region.Top = 5;
            region.Bottom = 6;
            region.Left = 7;
            region.Right = 8;
            Assert.Equal(5, region.Top);
            Assert.Equal(6, region.Bottom);
            Assert.Equal(7, region.Left);
            Assert.Equal(8, region.Right);
        }

        [Theory]
        [InlineData(-1, 0, 0, 0)]
        [InlineData(0, -1, 0, 0)]
        [InlineData(0, 0, -1, 0)]
        [InlineData(0, 0, 0, -1)]
        public void NegativeBoundsAreRejectedAtConstruction(int top, int bottom, int left, int right)
        {
            Assert.Throws<ArgumentException>(() => new Region(top, bottom, left, right));
        }

        [Fact]
        public void NegativeBoundsAreRejectedBySetters()
        {
            Region region = new Region(1, 2, 3, 4);
            Assert.Throws<ArgumentException>(() => region.Top = -1);
            Assert.Throws<ArgumentException>(() => region.Bottom = -1);
            Assert.Throws<ArgumentException>(() => region.Left = -1);
            Assert.Throws<ArgumentException>(() => region.Right = -1);
        }

        [Theory]
        [InlineData(0, 100, 0, 200, true)]
        [InlineData(100, 100, 0, 200, false)]   // degenerate: no height
        [InlineData(0, 100, 200, 200, false)]   // degenerate: no width
        [InlineData(0, 500, 0, 200, false)]     // taller than the screen
        [InlineData(0, 100, 0, 500, false)]     // wider than the screen
        [InlineData(400, 400, 0, 200, false)]   // starts past the bottom
        public void ValidityIsCheckedAgainstTheScreenSize(
            int top, int bottom, int left, int right, bool expected)
        {
            Assert.Equal(expected, new Region(top, bottom, left, right).IsValid(400, 300));
        }

        [Fact]
        public void IgnoreRegionIsARegion()
        {
            IgnoreRegion region = new IgnoreRegion(0, 10, 0, 20);
            Assert.Equal(10, region.Bottom);
            Assert.IsAssignableFrom<Region>(region);
        }
    }

    public class TileTests
    {
        [Fact]
        public void ALocalTileCarriesItsPathAndNoSha()
        {
            Tile tile = new Tile("/tmp/a.png", 60, 40, 10, 20, true);
            Dictionary<string, object?> payload = tile.ToPayload();

            Assert.Equal("/tmp/a.png", payload["filepath"]);
            Assert.Equal(60, payload["statusBarHeight"]);
            Assert.Equal(40, payload["navBarHeight"]);
            Assert.Equal(10, payload["headerHeight"]);
            Assert.Equal(20, payload["footerHeight"]);
            // Lower-cased on the wire: this is the CLI's spelling, not a typo.
            Assert.Equal(true, payload["fullscreen"]);
            Assert.Null(payload["sha"]);
        }

        [Fact]
        public void ARemotelyUploadedTileCarriesItsShaAndNoPath()
        {
            Dictionary<string, object?> payload =
                new Tile(null, 60, 0, 0, 0, false, "abc123").ToPayload();

            Assert.Null(payload["filepath"]);
            Assert.Equal("abc123", payload["sha"]);
        }

        [Fact]
        public void ATileListSerializesInOrder()
        {
            List<Dictionary<string, object?>> payload = Tile.ToPayload(new List<Tile>
            {
                new Tile("/a.png", 0, 0, 0, 0, false),
                new Tile("/b.png", 0, 0, 0, 0, false)
            });

            Assert.Equal(new[] { "/a.png", "/b.png" },
                payload.Select(t => t["filepath"]).ToArray());
        }
    }

    public class PercyPayloadTests
    {
        [Fact]
        public void PayloadsAreJsonSerialized()
        {
            Assert.Equal("{\"a\":1}", PercyPayload.PayloadParser(
                new Dictionary<string, object?> { ["a"] = 1 }));
        }

        [Fact]
        public void NullMembersAreWrittenToMatchTheOtherAppPercySdks()
        {
            // Those SDKs serialize with Newtonsoft, which emits nulls, so every CLI endpoint here
            // already accepts e.g. `"sha": null`. Dropping them would be an untested deviation.
            string json = PercyPayload.PayloadParser(new Dictionary<string, object?>
            {
                ["name"] = "x",
                ["sync"] = null
            });

            Assert.Equal("{\"name\":\"x\",\"sync\":null}", json);
        }

        [Fact]
        public void AlreadyJsonPayloadsPassThroughUntouched()
        {
            Assert.Equal("{\"raw\":true}", PercyPayload.PayloadParser("{\"raw\":true}", true));
            Assert.Equal("", PercyPayload.PayloadParser(null, true));
        }

        [Fact]
        public void ANullPayloadSerializesAsJsonNull()
        {
            Assert.Equal("null", PercyPayload.PayloadParser(null));
        }
    }

    public class WebDriverSessionTests : CoreTestBase
    {
        private const string Png = StubMobileDriver.ValidPngBase64;

        private static WebDriverSession Session(StubHttpMessageHandler handler,
            string server = "https://hub.example.com/wd/hub/", string sessionId = "s-1") =>
            new WebDriverSession(handler.Client(), server, sessionId);

        [Fact]
        public void TheEndpointIsTheStandardWebDriverScreenshotPath()
        {
            // A trailing slash on the server must not double up: some servers 404 on //session.
            Assert.Equal("https://hub.example.com/wd/hub/session/s-1/screenshot",
                Session(new StubHttpMessageHandler()).Endpoint);
        }

        [Fact]
        public void AW3cResponseYieldsTheImage()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":\"" + Png + "\"}");

            Assert.Equal(Png, Session(handler).TryGetScreenshotBase64());
        }

        [Fact]
        public void AJsonWireProtocolResponseYieldsTheImageToo()
        {
            // Which protocol a session speaks is the server's choice, not ours.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"status\":0,\"sessionId\":\"s-1\",\"value\":\"" + Png + "\"}");

            Assert.Equal(Png, Session(handler).TryGetScreenshotBase64());
        }

        [Fact]
        public void AnErrorStatusIsReportedWithItsCodeAndBody()
        {
            // A 404 (session gone) and a 500 (device unreachable) call for different responses from
            // whoever is reading, so the code has to survive into the log.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":{\"error\":\"invalid session id\"}}",
                    System.Net.HttpStatusCode.NotFound);

            Assert.Null(Session(handler).TryGetScreenshotBase64());
            Assert.True(Logged("refused a screenshot (404"));
            Assert.True(Logged("invalid session id"));
        }

        [Fact]
        public void AW3cErrorObjectUnderValueIsNotMistakenForAnImage()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":{\"error\":\"no such window\"}}");

            Assert.Null(Session(handler).TryGetScreenshotBase64());
            Assert.True(Logged("no image in it"));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("not json")]
        [InlineData("{\"value\":null}")]
        [InlineData("{\"value\":\"\"}")]
        public void AResponseWithNoUsableImageIsRejected(string body)
        {
            Assert.Null(Session(new StubHttpMessageHandler().Default(body)).TryGetScreenshotBase64());
        }

        [Fact]
        public void ATransportFailureIsRetriedBecauseTheDeviceMayNotBeReady()
        {
            // A Tosca step can hand over before the device is ready; the web Tosca SDK retries for the
            // same reason.
            StubHttpMessageHandler handler = new StubHttpMessageHandler();
            handler.Throw = new HttpRequestException("connection reset");

            Assert.Null(Session(handler).TryGetScreenshotBase64());
            Assert.Equal(WebDriverSession.Attempts, handler.Requests.Count);
        }

        [Fact]
        public void AServerErrorIsRetriedButAClientErrorIsNot()
        {
            // A 5xx may pass on a second look; a 404 says the session or endpoint is wrong and will
            // stay wrong, so retrying only delays the report.
            StubHttpMessageHandler serverError = new StubHttpMessageHandler()
                .Default("boom", System.Net.HttpStatusCode.InternalServerError);
            Assert.Null(Session(serverError).TryGetScreenshotBase64());
            Assert.Equal(WebDriverSession.Attempts, serverError.Requests.Count);

            StubHttpMessageHandler notFound = new StubHttpMessageHandler()
                .Default("no session", System.Net.HttpStatusCode.NotFound);
            Assert.Null(Session(notFound).TryGetScreenshotBase64());
            Assert.Single(notFound.Requests);
        }

        [Fact]
        public void ARetryThatSucceedsReturnsTheImage()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/screenshot", "boom", System.Net.HttpStatusCode.BadGateway)
                .On("/screenshot", "{\"value\":\"" + Png + "\"}");

            Assert.Equal(Png, Session(handler).TryGetScreenshotBase64());
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void AWellFormedRefusalIsNotRetried()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":{\"error\":\"no such window\"}}");

            Assert.Null(Session(handler).TryGetScreenshotBase64());
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void AnUnreachableServerIsReportedWithCredentialsRedacted()
        {
            // The endpoint commonly carries credentials in its userinfo, and this message is the one
            // most likely to be pasted into a support ticket.
            StubHttpMessageHandler handler = new StubHttpMessageHandler
            {
                Throw = new HttpRequestException("connect failed to https://user:key@hub.example.com")
            };

            Assert.Null(Session(handler, "https://user:key@hub.example.com/wd/hub")
                .TryGetScreenshotBase64());

            Assert.True(Logged("Could not reach the device session"));
            Assert.False(Logged("user:key"));
            Assert.True(Logged("***@hub.example.com"));
        }

        [Fact]
        public void AVeryLongBodyIsTruncatedSoALogLineStaysALogLine()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":{\"m\":\"" + new string('x', 5000) + "\"}}",
                    System.Net.HttpStatusCode.InternalServerError);

            Assert.Null(Session(handler).TryGetScreenshotBase64());
            Assert.True(Logs.Any(l => l.Message.Contains("\u2026") && l.Message.Length < 600));
        }

        [Fact]
        public void AnEmptyBodyIsDescribedRatherThanShownBlank()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("", System.Net.HttpStatusCode.BadGateway);

            Assert.Null(Session(handler).TryGetScreenshotBase64());
            Assert.True(Logged("(empty)"));
        }

        [Fact]
        public void CapabilitiesAreReadFromTheSession()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":{\"platformName\":\"Android\",\"deviceScreenSize\":\"1080x2400\"}}");

            IReadOnlyDictionary<string, object?> capabilities = Session(handler).TryGetCapabilities()!;

            Assert.Equal("Android", capabilities["platformName"]);
            Assert.EndsWith("/session/s-1", handler.Requests[0].Url);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"value\":\"not a map\"}")]
        [InlineData("not json")]
        public void UnusableCapabilitiesReadAsNone(string body)
        {
            Assert.Null(Session(new StubHttpMessageHandler().Default(body)).TryGetCapabilities());
        }

        [Fact]
        public void AnUnreachableSessionDegradesEachDeviceFactRatherThanTheRun()
        {
            // Every one of these has a module parameter that can supply it instead, so a session that
            // will not answer should cost the tag its detail and nothing more.
            SetEnv("PERCY_LOGLEVEL", "debug");
            StubHttpMessageHandler handler = new StubHttpMessageHandler
            {
                Throw = new HttpRequestException("connection reset")
            };
            WebDriverSession session = Session(handler);

            Assert.Null(session.TryGetCapabilities());
            Assert.Null(session.TryGetOrientation());
            Assert.Null(session.TryGetWindowWidth());
            Assert.True(Logged("Could not read the device session's"));
        }

        [Fact]
        public void ARefusedRequestForADeviceFactAlsoReadsAsAbsent()
        {
            SetEnv("PERCY_LOGLEVEL", "debug");
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("gone", System.Net.HttpStatusCode.NotFound);

            Assert.Null(Session(handler).TryGetOrientation());
            Assert.True(Logged("would not report its orientation"));
        }

        [Fact]
        public void TheWindowWidthFallsBackToTheOlderEndpointSpelling()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/window/rect", "gone", System.Net.HttpStatusCode.NotFound)
                .Default("{\"value\":{\"width\":390}}");

            Assert.Equal(390, Session(handler).TryGetWindowWidth());
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void AZeroWindowWidthIsTreatedAsNoAnswer()
        {
            // Zero is not a usable width, and reporting it would make the iOS scale factor nonsense.
            Assert.Null(Session(new StubHttpMessageHandler()
                .Default("{\"value\":{\"width\":0}}")).TryGetWindowWidth());
        }

        [Fact]
        public void AScriptRunsThroughTheW3cExecuteEndpoint()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":\"ok\"}");

            Assert.Equal("ok", Session(handler).ExecuteScript("browserstack_executor: {}"));
            Assert.Contains("/session/s-1/execute/sync", handler.Requests[0].Url);
            Assert.Contains("\"script\":\"browserstack_executor: {}\"", handler.Requests[0].Body!);
        }

        [Fact]
        public void ANonStringResultComesBackAsItsRawJson()
        {
            // The executor answers with an object for some commands, and the caller parses it.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("{\"value\":{\"success\":true}}");

            Assert.Equal("{\"success\":true}", Session(handler).ExecuteScript("x"));
        }

        [Fact]
        public void AHubWithoutTheW3cEndpointFallsBackToTheOlderSpelling()
        {
            // Which spelling a hub answers is its choice, not ours.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .On("/execute/sync", "nope", System.Net.HttpStatusCode.NotFound)
                .Default("{\"value\":\"ok\"}");

            Assert.Equal("ok", Session(handler).ExecuteScript("x"));
            Assert.Equal(2, handler.Requests.Count);
            Assert.EndsWith("/execute", handler.Requests[1].Url);
        }

        [Fact]
        public void NeitherEndpointExistingYieldsNothing()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("nope", System.Net.HttpStatusCode.NotFound);

            Assert.Null(Session(handler).ExecuteScript("x"));
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void ARefusedScriptIsNotRetriedOnTheOtherEndpoint()
        {
            // A 500 is about the script, and would fail identically on the other spelling.
            StubHttpMessageHandler handler = new StubHttpMessageHandler()
                .Default("bad command", System.Net.HttpStatusCode.InternalServerError);

            Assert.Null(Session(handler).ExecuteScript("x"));
            Assert.Single(handler.Requests);
            Assert.True(Logged("refused a script (500"));
        }

        [Fact]
        public void ASuccessfulResponseWithNoValueYieldsNothing()
        {
            Assert.Null(Session(new StubHttpMessageHandler().Default("{}")).ExecuteScript("x"));
        }

        [Fact]
        public void AnUnreachableHubIsReportedWhenRunningAScript()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler
            {
                Throw = new HttpRequestException("connect failed to https://user:key@hub.example.com")
            };

            Assert.Null(Session(handler).ExecuteScript("x"));
            Assert.True(Logged("Could not run a script"));
            Assert.False(Logged("user:key"));
        }

        [Fact]
        public void NullArgumentsAreRefusedAtConstruction()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler();
            Assert.Throws<ArgumentNullException>(() => new WebDriverSession(null!, "s", "i"));
            Assert.Throws<ArgumentNullException>(() => new WebDriverSession(handler.Client(), null!, "i"));
            Assert.Throws<ArgumentNullException>(() => new WebDriverSession(handler.Client(), "s", null!));
        }
    }

    public class SnapshotOutcomeTests
    {
        [Fact]
        public void AnAcceptedSnapshotIsReportedAsTaken()
        {
            Assert.Equal(SnapshotOutcome.Taken,
                SnapshotOutcome.Describe(Json.TryParse("{\"id\":\"c1\"}"), "home"));
        }

        [Fact]
        public void NoSnapshotIsNeverReportedAsTaken()
        {
            // The rule this class exists to hold: the step passes either way, so the message is the
            // only signal a reader gets. Claiming success when nothing reached Percy means nobody
            // goes looking — worse than an outright failure.
            string message = SnapshotOutcome.Describe(null, "home");

            Assert.NotEqual(SnapshotOutcome.Taken, message);
            Assert.Contains("No snapshot was recorded", message);
            // Names the snapshot, so a sheet with many steps points at the right one.
            Assert.Contains("home", message);
        }

        [Fact]
        public void ANotRunningCliIsReportedWithoutClaimingASnapshot()
        {
            Assert.NotEqual(SnapshotOutcome.Taken, SnapshotOutcome.PercyNotRunning);
            Assert.Contains("not running", SnapshotOutcome.PercyNotRunning);
        }
    }

    public class PercyExceptionTests
    {
        [Fact]
        public void ItCarriesAMessageAndAnOptionalCause()
        {
            PercyException bare = new PercyException("boom");
            Assert.Equal("boom", bare.Message);
            Assert.Null(bare.InnerException);

            InvalidOperationException cause = new InvalidOperationException("why");
            PercyException wrapped = new PercyException("boom", cause);
            Assert.Same(cause, wrapped.InnerException);
        }
    }
}
