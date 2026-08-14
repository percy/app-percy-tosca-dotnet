using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class ToscaOptionsTests : CoreTestBase
    {
        private static ToscaOptions.ParameterReader Reader(Dictionary<string, string?> values) =>
            name => values.TryGetValue(name, out string? value) ? value : null;

        private static ToscaOptions.ParameterReader Reader(params (string, string?)[] entries) =>
            Reader(entries.ToDictionary(e => e.Item1, e => e.Item2));

        [Fact]
        public void AnEmptyStepYieldsTheDocumentedDefaults()
        {
            ScreenshotOptions options = ToscaOptions.Build(Reader());

            Assert.False(options.FullScreen);
            Assert.False(options.FullPage);
            Assert.Null(options.ScreenLengths);
            // Zero, not null: an unset row means "trim nothing", which is what the hub is sent.
            Assert.Equal(0, options.TopScrollviewOffset);
            Assert.Equal(0, options.BottomScrollviewOffset);
            Assert.Empty(options.CustomIgnoreRegions);
        }

        [Fact]
        public void EveryTypedParameterIsRead()
        {
            ScreenshotOptions options = ToscaOptions.Build(Reader(
                ("ScreenLengths", "4"),
                ("FullScreen", "true"),
                ("FullPage", "yes"),
                ("IosOptimizedFullpage", "1"),
                ("TopScrollviewOffset", "10"),
                ("BottomScrollviewOffset", "20"),
                ("Labels", "smoke")));

            Assert.Equal(4, options.ScreenLengths);
            Assert.Equal(10, options.TopScrollviewOffset);
            Assert.Equal(20, options.BottomScrollviewOffset);
            Assert.True(options.FullScreen);
            Assert.True(options.FullPage);
            Assert.True(options.IosOptimizedFullpage);
            Assert.Equal("smoke", options.Labels);
        }

        [Theory]
        [InlineData("42", 42)]
        [InlineData(" 42 ", 42)]
        [InlineData("-5", -5)]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("  ", null)]
        [InlineData("1O80", null)]
        [InlineData("1.5", null)]
        public void ParseIntAcceptsIntegersAndRejectsTheRest(string? value, int? expected)
        {
            Assert.Equal(expected, ToscaOptions.ParseInt(value, "Width"));
        }

        [Fact]
        public void ParseIntReportsAValueItCouldNotRead()
        {
            // Silently treating a typo as unset would produce a wrong-sized tag with no clue why.
            ToscaOptions.ParseInt("1O80", "ScreenWidth");
            Assert.True(Logged("ScreenWidth"));
            Assert.True(Logged("1O80"));
        }

        [Fact]
        public void ParseIntNamesTheParameterGenericallyWhenNotGivenOne()
        {
            ToscaOptions.ParseInt("nope");
            Assert.True(Logged("parameter"));
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData(" True ", true)]
        [InlineData("1", true)]
        [InlineData("yes", true)]
        [InlineData("y", true)]
        [InlineData("false", false)]
        [InlineData("0", false)]
        [InlineData("no", false)]
        [InlineData("n", false)]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("maybe", null)]
        public void ParseBoolAcceptsTheSpellingsASheetCarries(string? value, bool? expected)
        {
            Assert.Equal(expected, ToscaOptions.ParseBool(value, "FullPage"));
        }

        [Fact]
        public void ParseBoolReportsAValueItCouldNotRead()
        {
            ToscaOptions.ParseBool("maybe", "FullPage");
            Assert.True(Logged("FullPage"));
        }

        [Fact]
        public void ParseBoolNamesTheParameterGenericallyWhenNotGivenOne()
        {
            ToscaOptions.ParseBool("maybe");
            Assert.True(Logged("parameter"));
        }

        [Fact]
        public void LocatorListsSplitOnSemicolonsWithoutBreakingXPathPredicates()
        {
            // The reason commas are not separators: this XPath contains one, and splitting on it
            // would turn one working locator into two that match nothing.
            List<string> parsed = ToscaOptions.ParseLocatorList(
                "//*[contains(@id,'total')] ; //button[@name='ok']");

            Assert.Equal(new[] { "//*[contains(@id,'total')]", "//button[@name='ok']" }, parsed);
        }

        [Fact]
        public void LocatorListsPreferNewlinesWhenPresentSoSemicolonsStayUsableInsideALocator()
        {
            List<string> parsed = ToscaOptions.ParseLocatorList(
                "//*[@x='a;b']\n//button[@name='ok']\r\n");

            Assert.Equal(new[] { "//*[@x='a;b']", "//button[@name='ok']" }, parsed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(";;;")]
        public void LocatorListsAreEmptyForNothingUsable(string? value)
        {
            Assert.Empty(ToscaOptions.ParseLocatorList(value));
        }

        [Fact]
        public void RegionsParseAsTopBottomLeftRight()
        {
            List<Region> regions = ToscaOptions.ParseRegions("0,100,0,200; 300,400,10,20");

            Assert.Equal(2, regions.Count);
            Assert.Equal(0, regions[0].Top);
            Assert.Equal(100, regions[0].Bottom);
            Assert.Equal(0, regions[0].Left);
            Assert.Equal(200, regions[0].Right);
            Assert.Equal(300, regions[1].Top);
        }

        [Fact]
        public void ARegionWithTheWrongNumberOfValuesIsReportedAndSkipped()
        {
            List<Region> regions = ToscaOptions.ParseRegions("0,100,0; 0,100,0,200", "CustomIgnoreRegions");

            Assert.Single(regions);
            Assert.True(Logged("expected four numbers"));
        }

        [Fact]
        public void ARegionWithAnUnreadableNumberIsSkipped()
        {
            List<Region> regions = ToscaOptions.ParseRegions("0,abc,0,200", "CustomIgnoreRegions");

            Assert.Empty(regions);
            Assert.True(Logged("abc"));
        }

        [Fact]
        public void ARegionWithANegativeBoundIsReportedAndSkipped()
        {
            // Region's constructor rejects these; the parser must report rather than propagate.
            List<Region> regions = ToscaOptions.ParseRegions("-1,100,0,200", "CustomIgnoreRegions");

            Assert.Empty(regions);
            Assert.True(Logged("Positive integer"));
        }

        [Fact]
        public void RegionsAreEmptyForABlankParameter()
        {
            Assert.Empty(ToscaOptions.ParseRegions(null));
        }

        [Fact]
        public void ABranchRowOutranksTheEnvironmentLikeTheOthers()
        {
            // A Tosca machine usually has no git repository, so this row is often the only thing that
            // names the branch a build belongs to.
            SetEnv("PERCY_BRANCH", "from-environment");
            ToscaOptions.ApplyEnvironment(Reader(("Branch", "from-the-sheet")));

            Assert.Equal("from-the-sheet", Env.Read("PERCY_BRANCH"));
        }

        [Fact]
        public void WithNoBranchRowTheEnvironmentStillDecides()
        {
            SetEnv("PERCY_BRANCH", "from-environment");
            ToscaOptions.ApplyEnvironment(Reader());

            Assert.Equal("from-environment", Env.Read("PERCY_BRANCH"));
        }

        [Fact]
        public void KnownParametersListsEveryParameterTheBuildersRead()
        {
            // A parameter Build() reads without being listed here is unreachable: the manifest is what
            // the module's rows are built from.
            List<string> read = new List<string>();
            ToscaOptions.Build(name => { read.Add(name); return null; });

            Assert.Empty(read.Distinct().Except(ToscaOptions.KnownParameters));
        }

        [Fact]
        public void KnownParametersListsEveryEnvironmentParameter()
        {
            // An environment parameter missing from the manifest is unreachable, documented or not.
            Assert.Empty(ToscaOptions.EnvironmentParameters
                .Select(entry => entry.Parameter)
                .Except(ToscaOptions.KnownParameters));
        }

        [Fact]
        public void ApplyEnvironmentPutsEveryParameterInForce()
        {
            ToscaOptions.ApplyEnvironment(Reader(
                ("Branch", "release-24"),
                ("LogLevel", "debug"),
                ("LogFile", "/tmp/percy-from-tosca.txt"),
                ("CliApi", "http://elsewhere:1234"),
                ("TmpDir", "/tmp/percy-tiles"),
                ("ForceFullPage", "true"),
                ("DisableRemoteUploads", "true"),
                ("EnablePercyDev", "true"),
                ("AutomateDomain", "my-hub.internal")));

            Assert.True(Env.Debug());
            // No Env accessor: the CLI reads this one, and the SDK only has to carry it to the child.
            Assert.Equal("release-24", Env.Read("PERCY_BRANCH"));
            Assert.Equal("/tmp/percy-from-tosca.txt", Env.LogFile());
            Assert.Equal("http://elsewhere:1234", Env.CliApi());
            Assert.Equal("/tmp/percy-tiles", Env.TempDir());
            Assert.True(Env.ForceFullPage());
            Assert.True(Env.DisableRemoteUploads());
            Assert.True(Env.EnablePercyDev());
            Assert.Equal("my-hub.internal", Env.AutomateDomain());
        }

        [Fact]
        public void ApplyEnvironmentClearsARowThatIsNoLongerOnTheSheet()
        {
            ToscaOptions.ApplyEnvironment(Reader(("LogLevel", "debug")));
            Assert.True(Env.Debug());

            // The next step's sheet has no LogLevel row; the previous step's value must not outlive it.
            ToscaOptions.ApplyEnvironment(Reader());
            Assert.False(Env.Debug());
        }
    }
}
