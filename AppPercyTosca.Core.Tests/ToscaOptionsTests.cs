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

            // -1 is "the step did not say", which the metadata layer distinguishes from a real
            // zero-height bar.
            Assert.Equal(-1, options.StatusBarHeight);
            Assert.Equal(-1, options.NavBarHeight);
            Assert.Equal(0, options.ScreenWidth);
            Assert.Equal(0, options.ScreenHeight);
            Assert.False(options.FullScreen);
            Assert.False(options.FullPage);
            // Null, not false: the CLI applies its own sync default when we say nothing.
            Assert.Null(options.Sync);
            Assert.Null(options.ScreenLengths);
            Assert.Null(options.DeviceName);
            Assert.Empty(options.IgnoreRegionXpaths);
            Assert.Empty(options.CustomIgnoreRegions);
        }

        [Fact]
        public void StringParametersAreTrimmedAndBlanksBecomeNull()
        {
            ScreenshotOptions options = ToscaOptions.Build(Reader(
                ("DeviceName", "  Pixel 7  "),
                ("OsName", "\tAndroid\t"),
                ("OsVersion", "   "),
                ("TestCase", "checkout")));

            Assert.Equal("Pixel 7", options.DeviceName);
            Assert.Equal("Android", options.OsName);
            Assert.Null(options.PlatformVersion);
            Assert.Equal("checkout", options.TestCase);
        }

        [Fact]
        public void EveryTypedParameterIsRead()
        {
            ScreenshotOptions options = ToscaOptions.Build(Reader(
                ("StatusBarHeight", "60"),
                ("NavBarHeight", "40"),
                ("ScreenWidth", "1080"),
                ("ScreenHeight", "2340"),
                ("TopScrollviewOffset", "10"),
                ("BottomScrollviewOffset", "20"),
                ("ScreenLengths", "4"),
                ("FullScreen", "true"),
                ("FullPage", "yes"),
                ("IosOptimizedFullpage", "1"),
                ("Sync", "false"),
                ("Orientation", "landscape"),
                ("ScrollableXpath", "//scroll"),
                ("ScrollableId", "list"),
                ("Labels", "smoke"),
                ("ThTestCaseExecutionId", "exec-1")));

            Assert.Equal(60, options.StatusBarHeight);
            Assert.Equal(40, options.NavBarHeight);
            Assert.Equal(1080, options.ScreenWidth);
            Assert.Equal(2340, options.ScreenHeight);
            Assert.Equal(10, options.TopScrollviewOffset);
            Assert.Equal(20, options.BottomScrollviewOffset);
            Assert.Equal(4, options.ScreenLengths);
            Assert.True(options.FullScreen);
            Assert.True(options.FullPage);
            Assert.True(options.IosOptimizedFullpage);
            Assert.False(options.Sync);
            Assert.Equal("landscape", options.Orientation);
            Assert.Equal("//scroll", options.ScrollableXpath);
            Assert.Equal("list", options.ScrollableId);
            Assert.Equal("smoke", options.Labels);
            Assert.Equal("exec-1", options.ThTestCaseExecutionId);
        }

        [Fact]
        public void AStatusBarOfZeroIsKeptDistinctFromUnset()
        {
            ScreenshotOptions options = ToscaOptions.Build(Reader(("StatusBarHeight", "0")));
            Assert.Equal(0, options.StatusBarHeight);
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
        public void RawOptionsParseAsAJsonObject()
        {
            Dictionary<string, object?> parsed =
                ToscaOptions.ParseRawOptions("{\"freeze_animated_image\": true, \"n\": 2}");

            Assert.Equal(true, parsed["freeze_animated_image"]);
            Assert.Equal(2L, parsed["n"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void RawOptionsAreEmptyWhenUnset(string? value)
        {
            Assert.Empty(ToscaOptions.ParseRawOptions(value));
        }

        [Theory]
        [InlineData("[1,2]")]
        [InlineData("not json")]
        public void RawOptionsThatAreNotAnObjectAreReportedAndIgnored(string value)
        {
            Assert.Empty(ToscaOptions.ParseRawOptions(value));
            Assert.True(Logged("not a JSON object"));
        }

        [Fact]
        public void AutomateOptionsOmitAnythingTheStepDidNotSet()
        {
            // Sending an explicit null would override a project-level default rather than defer
            // to it, so unset parameters must be absent from the bag entirely.
            Dictionary<string, object?> options = ToscaOptions.BuildAutomateOptions(Reader());

            Assert.Empty(options);
        }

        [Fact]
        public void AutomateOptionsUseTheCliSnakeCaseSpellings()
        {
            Dictionary<string, object?> options = ToscaOptions.BuildAutomateOptions(Reader(
                ("DeviceName", "Pixel 7"),
                ("Orientation", "portrait"),
                ("StatusBarHeight", "60"),
                ("NavBarHeight", "40"),
                ("TopScrollviewOffset", "5"),
                ("BottomScrollviewOffset", "6"),
                ("ScreenLengths", "3"),
                ("FullScreen", "true"),
                ("FullPage", "true"),
                ("IosOptimizedFullpage", "true"),
                ("Sync", "true"),
                ("TestCase", "tc"),
                ("Labels", "l"),
                ("ThTestCaseExecutionId", "e"),
                ("ScrollableXpath", "//s"),
                ("ScrollableId", "sid")));

            Assert.Equal("Pixel 7", options["device_name"]);
            Assert.Equal("portrait", options["orientation"]);
            Assert.Equal(60, options["status_bar_height"]);
            Assert.Equal(40, options["nav_bar_height"]);
            Assert.Equal(5, options["top_scrollview_offset"]);
            Assert.Equal(6, options["bottom_scrollview_offset"]);
            Assert.Equal(3, options["screen_lengths"]);
            Assert.Equal(true, options["full_screen"]);
            Assert.Equal(true, options["full_page"]);
            Assert.Equal(true, options["ios_optimized_fullpage"]);
            Assert.Equal(true, options["sync"]);
            Assert.Equal("tc", options["test_case"]);
            Assert.Equal("l", options["labels"]);
            Assert.Equal("e", options["th_test_case_execution_id"]);
            Assert.Equal("//s", options["scrollable_xpath"]);
            Assert.Equal("sid", options["scrollable_id"]);
        }

        [Fact]
        public void AutomateOptionsCarryRegionListsUnderTheKeysTheCliResolves()
        {
            Dictionary<string, object?> options = ToscaOptions.BuildAutomateOptions(Reader(
                ("IgnoreRegionXpaths", "//a"),
                ("ConsiderRegionXpaths", "//b"),
                ("CustomIgnoreRegions", "0,10,0,20"),
                ("CustomConsiderRegions", "1,11,1,21")));

            Assert.Equal(new[] { "//a" }, Assert.IsType<List<string>>(options["ignore_region_xpaths"]));
            Assert.Equal(new[] { "//b" }, Assert.IsType<List<string>>(options["consider_region_xpaths"]));

            List<Dictionary<string, object?>> ignored =
                Assert.IsType<List<Dictionary<string, object?>>>(options["custom_ignore_regions"]);
            Assert.Equal(10, ignored[0]["bottom"]);

            List<Dictionary<string, object?>> considered =
                Assert.IsType<List<Dictionary<string, object?>>>(options["custom_consider_regions"]);
            Assert.Equal(21, considered[0]["right"]);
        }

        [Fact]
        public void XPathsAreNotSentUnderTheAppiumElementKey()
        {
            // That key means "a list of Appium element objects". Locators sent under it are routed
            // through local element resolution, which a Tosca session cannot do, so every region is
            // dropped — the exact silent failure this spelling exists to avoid.
            Dictionary<string, object?> options = ToscaOptions.BuildAutomateOptions(Reader(
                ("IgnoreRegionXpaths", "//a"),
                ("ConsiderRegionXpaths", "//b")));

            Assert.False(options.ContainsKey(PercyOnAutomate.IgnoreElementKey));
            Assert.False(options.ContainsKey(PercyOnAutomate.ConsiderElementKey));
        }

        [Fact]
        public void FullPageUsesTheSeparatedSpellingTheCliCamelCasesCorrectly()
        {
            // The CLI camelCases option keys and reads `fullPage`. "fullpage" has no separator, so it
            // survives that conversion unchanged and never matches — full page capture would silently
            // degrade to one screen.
            Dictionary<string, object?> options =
                ToscaOptions.BuildAutomateOptions(Reader(("FullPage", "true")));

            Assert.Equal(true, options["full_page"]);
            Assert.False(options.ContainsKey("fullpage"));
        }

        [Theory]
        [InlineData("IgnoreRegionAccessibilityIds")]
        [InlineData("ConsiderRegionAccessibilityIds")]
        public void AccessibilityIdRegionsAreReportedAsUnsupportedRatherThanSilentlyDropped(string parameter)
        {
            // Resolving them needs a driver Tosca does not expose, and Percy on Automate has no
            // accessibility-id option — so forwarding them would leave the region unapplied with
            // nothing said about it.
            Dictionary<string, object?> options =
                ToscaOptions.BuildAutomateOptions(Reader((parameter, "id-1")));

            Assert.DoesNotContain("accessibility", string.Join(",", options.Keys));
            Assert.True(Logged(parameter));
            Assert.True(Logged("CustomIgnoreRegions"));
        }

        [Fact]
        public void UnsetAccessibilityIdParametersAreNotWarnedAbout()
        {
            ToscaOptions.BuildAutomateOptions(Reader());
            Assert.False(Logged("not supported on Tosca"));
        }

        [Fact]
        public void TheRawOptionsParameterIsMergedLastSoItCanReachAnythingUnnamed()
        {
            Dictionary<string, object?> options = ToscaOptions.BuildAutomateOptions(Reader(
                ("FullScreen", "true"),
                ("Options", "{\"full_screen\": false, \"brand_new_cli_option\": \"x\"}")));

            Assert.Equal(false, options["full_screen"]);
            Assert.Equal("x", options["brand_new_cli_option"]);
        }

        [Fact]
        public void KnownParametersListsEveryParameterTheBuildersRead()
        {
            // The shim reads this to declare the module's parameter rows, so a parameter added to
            // Build() without being listed here would be unreachable from Tosca.
            List<string> read = new List<string>();
            ToscaOptions.Build(name => { read.Add(name); return null; });
            ToscaOptions.BuildAutomateOptions(name => { read.Add(name); return null; });

            Assert.Empty(read.Distinct().Except(ToscaOptions.KnownParameters));
        }
    }
}
