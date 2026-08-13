using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class PercyCliLifecycleTests : CoreTestBase
    {
        private const string HealthyBody =
            "{\"success\":true,\"build\":{\"id\":\"build-1\",\"url\":\"https://percy.io/b/1\"}}";

        private static PercyClient Healthy() =>
            new PercyClient(new StubHttpMessageHandler().Default(HealthyBody).Client(),
                "http://localhost:5338");

        /// Down first, then up: what the start task actually sees — a healthcheck that fails before the
        /// CLI exists and succeeds once it does.
        private static PercyClient ComesUp()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler();
            handler.Throw = new HttpRequestException("connection refused");
            PercyClient client = new PercyClient(handler.Client(), "http://localhost:5338");
            Assert.False(client.Healthcheck());
            handler.Throw = null;
            handler.Default(HealthyBody);
            return client;
        }

        private static PercyClient Down()
        {
            StubHttpMessageHandler handler = new StubHttpMessageHandler
            {
                Throw = new HttpRequestException("connection refused")
            };
            return new PercyClient(handler.Client(), "http://localhost:5338");
        }

        [Fact]
        public void AnAlreadyRunningCliIsLeftAloneRatherThanStartedTwice()
        {
            // A second CLI on the same port fails with an error about the port rather than the mistake,
            // and would serve a different build to the steps that follow.
            StubCliProcess process = new StubCliProcess();
            StubCliLauncher launcher = new StubCliLauncher(process);

            Assert.Equal(PercyCliLifecycle.AlreadyRunning,
                PercyCliLifecycle.Start(launcher, Healthy()));
            Assert.Empty(launcher.Arguments);
        }

        [Fact]
        public void TheReadyLineFollowedByAHealthyCliIsASuccessfulStart()
        {
            StubCliLauncher launcher = new StubCliLauncher(
                new StubCliProcess("[percy] Percy has started!"));

            Assert.Equal(PercyCliLifecycle.Started, PercyCliLifecycle.Start(launcher, ComesUp()));
            Assert.Equal(new[] { "app:exec:start" }, launcher.Arguments);
        }

        [Fact]
        public void TheStartedBuildUrlIsReportedBecauseTheHealthcheckRecordedIt()
        {
            PercyCliLifecycle.Start(new StubCliLauncher(new StubCliProcess("Percy has started!")),
                ComesUp());

            Assert.True(Logged("https://percy.io/b/1"));
        }

        [Fact]
        public void ACliThatSaysItStartedButDoesNotAnswerIsNotCalledStarted()
        {
            // The log line is the CLI's presentation; the healthcheck is the contract. Trusting only the
            // line would report success to a sheet whose snapshots will all be dropped.
            StubCliProcess process = new StubCliProcess("Percy has started!");

            string outcome = PercyCliLifecycle.Start(new StubCliLauncher(process), Down());

            Assert.NotEqual(PercyCliLifecycle.Started, outcome);
            Assert.True(Logged("not answering its healthcheck"));
            Assert.Equal(1, process.KillCount);
        }

        [Fact]
        public void ACliThatExitsInsteadOfStartingSaysSoWithItsExitCode()
        {
            StubCliProcess process = new StubCliProcess { HasExited = true, ExitCode = 1 };

            string outcome = PercyCliLifecycle.Start(new StubCliLauncher(process), Down());

            Assert.NotEqual(PercyCliLifecycle.Started, outcome);
            Assert.True(Logged("exited with code 1"));
            Assert.True(Logged("PERCY_TOKEN"));
            Assert.Equal(1, process.KillCount);
        }

        [Fact]
        public void ALiveCliThatGoesQuietWithoutSayingItStartedIsAFailedStart()
        {
            // Not exited, but nothing more to read: the same outcome from here, and without the exit
            // code there is nothing to say about why.
            StubCliProcess process = new StubCliProcess("[percy] Notice: something else");

            Assert.NotEqual(PercyCliLifecycle.Started,
                PercyCliLifecycle.Start(new StubCliLauncher(process), Down()));
            Assert.False(Logged("exited with code"));
        }

        [Fact]
        public void AFailedStartRepeatsWhatTheCliSaidWithoutNeedingDebugLogging()
        {
            // The whole output at info: it is the only explanation there is, and a user who has to
            // re-run with LogLevel=debug to see it has already lost the run.
            PercyCliLifecycle.Start(
                new StubCliLauncher(new StubCliProcess("[percy] Missing PERCY_TOKEN")), Down());

            Assert.True(Logged("The Percy CLI said:"));
            Assert.True(Logged("percy: [percy] Missing PERCY_TOKEN"));
            Assert.DoesNotContain("debug", Logs.Where(l => l.Message.Contains("Missing PERCY_TOKEN"))
                .Select(l => l.Level));
        }

        [Fact]
        public void ACliThatPrintsNothingAtAllSaysWhereToLookInstead()
        {
            PercyCliLifecycle.Start(new StubCliLauncher(new StubCliProcess()), Down());

            Assert.True(Logged("printed nothing before giving up"));
            Assert.True(Logged("CliCommand"));
        }

        [Fact]
        public void ASuccessfulStartDoesNotNarrateItself()
        {
            // Every line at info would make a working start noisy for no benefit; the failure path is
            // where the transcript earns its place.
            PercyCliLifecycle.Start(
                new StubCliLauncher(new StubCliProcess("[percy] Percy has started!")), ComesUp());

            Assert.False(Logged("The Percy CLI said:"));
        }

        [Fact]
        public void AStartThatNeverReportsReadyGivesUpAtTheTimeout()
        {
            // Clock jumps past the deadline on the second reading, so the loop exits on time rather
            // than on the stub running out of lines.
            Queue<DateTime> readings = new Queue<DateTime>(new[]
            {
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            });
            PercyCliLifecycle.Clock = () =>
                readings.Count > 1 ? readings.Dequeue() : readings.Peek();

            StubCliProcess process = new StubCliProcess("[percy] still working");

            Assert.NotEqual(PercyCliLifecycle.Started,
                PercyCliLifecycle.Start(new StubCliLauncher(process), Down()));
            Assert.True(Logged("did not report"));
            Assert.Equal(1, process.KillCount);
        }

        [Fact]
        public void ALauncherThatThrowsMidStartDoesNotLeaveAProcessBehind()
        {
            StubCliProcess process = new StubCliProcess("Percy has started!");
            PercyCliLifecycle.Clock = () => throw new InvalidOperationException("clock exploded");

            Assert.Throws<InvalidOperationException>(() =>
                PercyCliLifecycle.Start(new StubCliLauncher(process), Down()));
            Assert.Equal(1, process.KillCount);
        }

        [Fact]
        public void StartRefusesNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => PercyCliLifecycle.Start(null!, Down()));
            Assert.Throws<ArgumentNullException>(() =>
                PercyCliLifecycle.Start(new StubCliLauncher(new StubCliProcess()), null!));
        }

        [Fact]
        public void StopReportsTheBuildLinkTheCliFinalized()
        {
            StubCliLauncher launcher = new StubCliLauncher(new StubCliProcess(
                "[percy] Stopping percy...",
                "[percy] Finalized build #14: https://percy.io/1db/app/proj/builds/52684909"));

            string outcome = PercyCliLifecycle.Stop(launcher);

            Assert.Contains("https://percy.io/1db/app/proj/builds/52684909", outcome);
            Assert.Equal(new[] { "app:exec:stop" }, launcher.Arguments);
        }

        [Fact]
        public void TheLastLinkWinsBecauseTheEarlierOneIsNotTheFinalizedBuild()
        {
            string outcome = PercyCliLifecycle.Stop(new StubCliLauncher(new StubCliProcess(
                "[percy] Finalizing build https://percy.io/first",
                "[percy] Finalized build #2: https://percy.io/second")));

            Assert.Contains("https://percy.io/second", outcome);
            Assert.DoesNotContain("https://percy.io/first", outcome);
        }

        [Theory]
        [InlineData("see https://percy.io/b/9.", "https://percy.io/b/9")]
        [InlineData("see https://percy.io/b/9,", "https://percy.io/b/9")]
        [InlineData("see (https://percy.io/b/9)", "https://percy.io/b/9")]
        public void SentencePunctuationIsNotPartOfTheLink(string line, string expected)
        {
            string outcome = PercyCliLifecycle.Stop(new StubCliLauncher(new StubCliProcess(line)));

            Assert.EndsWith(expected, outcome);
        }

        [Fact]
        public void AStopThatPrintsNoLinkFallsBackToTheOneTheHealthcheckRecorded()
        {
            Env.PercyBuildUrl = "https://percy.io/from-healthcheck";

            string outcome = PercyCliLifecycle.Stop(
                new StubCliLauncher(new StubCliProcess("[percy] Stopping percy...")));

            Assert.Contains("https://percy.io/from-healthcheck", outcome);
        }

        [Fact]
        public void WithNoLinkAnywhereStopStillReportsThatItStopped()
        {
            Assert.Equal(PercyCliLifecycle.Stopped,
                PercyCliLifecycle.Stop(new StubCliLauncher(new StubCliProcess())));
        }

        [Fact]
        public void StopDisposesTheProcessItStarted()
        {
            StubCliProcess process = new StubCliProcess("[percy] done");

            PercyCliLifecycle.Stop(new StubCliLauncher(process));

            Assert.True(process.Disposed);
        }

        [Fact]
        public void StopRefusesANullLauncher()
        {
            Assert.Throws<ArgumentNullException>(() => PercyCliLifecycle.Stop(null!));
        }
    }
}
