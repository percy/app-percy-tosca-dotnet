using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class MetadataResolverTests : CoreTestBase
    {
        private static Metadata Resolve(StubMobileDriver driver, ScreenshotOptions? options = null) =>
            MetadataResolver.Resolve(driver, options ?? new ScreenshotOptions(),
                new Cache<string, object?>());

        [Theory]
        [InlineData("Android")]
        [InlineData("android")]
        [InlineData("ANDROID")]
        public void AnAndroidSessionResolvesToAndroidMetadata(string platform)
        {
            StubMobileDriver driver = new StubMobileDriver { PlatformName = platform };
            Assert.IsType<AndroidMetadata>(Resolve(driver));
        }

        [Theory]
        [InlineData("iOS")]
        [InlineData("ios")]
        [InlineData("iPhone")]
        [InlineData("iPad")]
        public void AniOSSessionResolvesToIosMetadata(string platform)
        {
            StubMobileDriver driver = new StubMobileDriver { PlatformName = platform };
            Assert.IsType<IosMetadata>(Resolve(driver));
        }

        [Fact]
        public void TheOsNameParameterOverridesWhatTheSessionReports()
        {
            // Tosca's Mobile engine may not expose platformName at all, so OsName on the module is
            // the documented way to say what the device is.
            StubMobileDriver driver = new StubMobileDriver { PlatformName = null };
            Assert.IsType<IosMetadata>(Resolve(driver, new ScreenshotOptions { OsName = "iOS" }));
        }

        [Fact]
        public void AnUnknownPlatformIsNamedRatherThanSilentlyDefaulted()
        {
            // Defaulting would yield a tag with the wrong dimensions, which surfaces as a
            // whole-screen visual diff that is very hard to trace back to a missing capability.
            StubMobileDriver driver = new StubMobileDriver { PlatformName = "Windows Phone" };

            PercyException error = Assert.Throws<PercyException>(() => Resolve(driver));
            Assert.Contains("Windows Phone", error.Message);
            Assert.Contains("OsName", error.Message);
        }

        [Fact]
        public void ASessionWithNoPlatformAtAllStillExplainsWhatToSet()
        {
            StubMobileDriver driver = new StubMobileDriver { PlatformName = null };

            PercyException error = Assert.Throws<PercyException>(() => Resolve(driver));
            Assert.Contains("OsName", error.Message);
        }
    }

    public class MetadataSharedTests : CoreTestBase
    {
        private static Metadata Build(StubMobileDriver driver, ScreenshotOptions options) =>
            MetadataResolver.Resolve(driver, options, new Cache<string, object?>());

        [Theory]
        [InlineData("portrait", "portrait")]
        [InlineData("LANDSCAPE", "landscape")]
        [InlineData(" Portrait ", "portrait")]
        public void AnExplicitOrientationIsUsedAsGiven(string supplied, string expected)
        {
            Metadata metadata = Build(StubMobileDriver.Android(),
                new ScreenshotOptions { Orientation = supplied });

            Assert.Equal(expected, metadata.Orientation());
        }

        [Fact]
        public void AutoAsksTheDevice()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Orientation = "LANDSCAPE";

            Metadata metadata = Build(driver, new ScreenshotOptions { Orientation = "auto" });

            Assert.Equal("landscape", metadata.Orientation());
        }

        [Fact]
        public void AutoFallsBackToPortraitWhenTheDeviceWillNotSay()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Orientation = null;

            Assert.Equal("portrait",
                Build(driver, new ScreenshotOptions { Orientation = "auto" }).Orientation());
        }

        [Fact]
        public void AMisspelledOrientationBecomesPortrait()
        {
            Assert.Equal("portrait", Build(StubMobileDriver.Android(),
                new ScreenshotOptions { Orientation = "sideways" }).Orientation());
        }

        [Fact]
        public void WithNothingSuppliedTheOrientationCapabilityIsUsed()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["orientation"] = "LANDSCAPE";

            Assert.Equal("landscape", Build(driver, new ScreenshotOptions()).Orientation());
        }

        [Fact]
        public void TheDeviceIsAskedWhenNoParameterOrCapabilityCarriesAnOrientation()
        {
            // The other App Percy SDKs default to portrait here and only ask when told "auto". This one
            // is already talking to the session, and defaulting to portrait on a landscape device gets
            // the dimensions wrong too.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Orientation = "LANDSCAPE";

            Assert.Equal("landscape", Build(driver, new ScreenshotOptions()).Orientation());
        }

        [Fact]
        public void WithNoOrientationAnywhereAndASilentDeviceItIsPortrait()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Orientation = null;

            Assert.Equal("portrait", Build(driver, new ScreenshotOptions()).Orientation());
        }

        [Fact]
        public void ALandscapeDeviceReportsItsScreenTheWayRoundTheScreenshotIs()
        {
            // A platform reports the physical screen — 1080x2400 whichever way the phone is held — but
            // the image Percy diffs is 2400x1080. Left unswapped the tag disagrees with the image,
            // which splits the baseline and fails every custom pixel region.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Orientation = "LANDSCAPE";
            Metadata metadata = Build(driver, new ScreenshotOptions());

            Assert.Equal(2340, metadata.DeviceScreenWidth());
            Assert.Equal(1080, metadata.DeviceScreenHeight());

            Dictionary<string, object?> tag = metadata.GetTag();
            Assert.Equal(2340, tag["width"]);
            Assert.Equal(1080, tag["height"]);
            Assert.Equal("landscape", tag["orientation"]);
        }

        [Fact]
        public void APortraitDeviceIsLeftAlone()
        {
            Metadata metadata = Build(StubMobileDriver.Android(), new ScreenshotOptions());

            Assert.Equal(1080, metadata.DeviceScreenWidth());
            Assert.Equal(2340, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void APlatformThatAlreadyAccountsForRotationIsNotReversed()
        {
            // Only a portrait-shaped report is swapped; a platform that has already rotated its answer
            // would otherwise have a correct value turned wrong.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Orientation = "LANDSCAPE";
            driver.Caps["deviceScreenSize"] = "2340x1080";
            Metadata metadata = Build(driver, new ScreenshotOptions());

            Assert.Equal(2340, metadata.DeviceScreenWidth());
            Assert.Equal(1080, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void AnExplicitOsVersionWinsOverTheCapability()
        {
            Assert.Equal("14", Build(StubMobileDriver.Android(),
                new ScreenshotOptions { PlatformVersion = "14" }).PlatformVersion());
        }

        [Fact]
        public void TheOsVersionFallsBackThroughBothCapabilitySpellings()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            Assert.Equal("13", Build(driver, new ScreenshotOptions()).PlatformVersion());

            driver.Caps.Remove("platformVersion");
            driver.Caps["os_version"] = "12";
            Assert.Equal("12", Build(driver, new ScreenshotOptions()).PlatformVersion());

            driver.Caps.Remove("os_version");
            Assert.Null(Build(driver, new ScreenshotOptions()).PlatformVersion());
        }

        [Fact]
        public void TheTagCarriesEverythingPercyGroupsComparisonsBy()
        {
            Metadata metadata = Build(StubMobileDriver.Android(), new ScreenshotOptions());

            Dictionary<string, object?> tag = metadata.GetTag();

            Assert.Equal("Samsung Galaxy S22", tag["name"]);
            Assert.Equal("Android", tag["osName"]);
            Assert.Equal("13", tag["osVersion"]);
            Assert.Equal(1080, tag["width"]);
            Assert.Equal(2340, tag["height"]);
            Assert.Equal("portrait", tag["orientation"]);
        }
    }

    public class AndroidMetadataTests : CoreTestBase
    {
        private static AndroidMetadata Build(StubMobileDriver driver, ScreenshotOptions? options = null) =>
            new AndroidMetadata(driver, options ?? new ScreenshotOptions(), new Cache<string, object?>());

        [Fact]
        public void TheOsNameIsAndroid()
        {
            Assert.Equal("Android", Build(StubMobileDriver.Android()).OsName());
        }

        [Fact]
        public void TheDeviceNameFallsBackFromSuppliedToResolvedToRequested()
        {
            StubMobileDriver driver = StubMobileDriver.Android();

            Assert.Equal("Pixel 7",
                Build(driver, new ScreenshotOptions { DeviceName = "Pixel 7" }).DeviceName());

            // `device` is what App Automate reports it actually allocated.
            Assert.Equal("Samsung Galaxy S22", Build(driver).DeviceName());

            driver.Caps.Remove("device");
            driver.Caps["deviceName"] = "Galaxy S22 Ultra";
            Assert.Equal("Galaxy S22 Ultra", Build(driver).DeviceName());

            driver.Caps.Remove("deviceName");
            Assert.Null(Build(driver).DeviceName());
        }

        [Fact]
        public void ScreenDimensionsComeFromDeviceScreenSize()
        {
            AndroidMetadata metadata = Build(StubMobileDriver.Android());

            Assert.Equal(1080, metadata.DeviceScreenWidth());
            Assert.Equal(2340, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void SuppliedDimensionsWinOverTheCapability()
        {
            AndroidMetadata metadata = Build(StubMobileDriver.Android(),
                new ScreenshotOptions { ScreenWidth = 720, ScreenHeight = 1280 });

            Assert.Equal(720, metadata.DeviceScreenWidth());
            Assert.Equal(1280, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void TheBarsAreDerivedFromTheViewportRect()
        {
            AndroidMetadata metadata = Build(StubMobileDriver.Android(statusBar: 60, navBar: 40));

            Assert.Equal(60, metadata.StatBarHeight());
            Assert.Equal(40, metadata.NavBarHeight());
        }

        [Fact]
        public void SuppliedBarHeightsWinAndZeroIsHonoured()
        {
            AndroidMetadata metadata = Build(StubMobileDriver.Android(),
                new ScreenshotOptions { StatusBarHeight = 0, NavBarHeight = 0 });

            // 0 must mean "no bar", not "the step said nothing" — that is why the sentinel is -1.
            Assert.Equal(0, metadata.StatBarHeight());
            Assert.Equal(0, metadata.NavBarHeight());
        }

        [Fact]
        public void WithNoViewportRectTheBarsAreZeroRatherThanGuessed()
        {
            // This is the shape a Tosca mobile session is most likely to have.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("viewportRect");

            AndroidMetadata metadata = Build(driver);

            Assert.Equal(0, metadata.StatBarHeight());
            Assert.Equal(0, metadata.NavBarHeight());
            // deviceScreenSize is still there, so the tag is still right.
            Assert.Equal(2340, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void ADeviceWhoseViewportSpansTheScreenHasNoNavBarToTrim()
        {
            // A negative value here would be sent on as a crop and corrupt the comparison.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["viewportRect"] = new Dictionary<string, object?>
            {
                ["top"] = 60,
                ["height"] = 2340
            };

            Assert.Equal(0, Build(driver).NavBarHeight());
        }

        [Fact]
        public void WithoutDeviceScreenSizeTheHeightIsRebuiltFromTheViewportPlusTheBars()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("deviceScreenSize");

            AndroidMetadata metadata = Build(driver,
                new ScreenshotOptions { StatusBarHeight = 60, NavBarHeight = 40 });

            Assert.Equal(2240 + 60 + 40, metadata.DeviceScreenHeight());
            Assert.Equal(1080, metadata.DeviceScreenWidth());
        }

        [Fact]
        public void WithoutDeviceScreenSizeTheNavBarCannotBeDerivedAndIsZero()
        {
            // Deriving it from DeviceScreenHeight() would recurse, since that falls back to adding
            // this value on.
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("deviceScreenSize");

            Assert.Equal(0, Build(driver).NavBarHeight());
        }

        [Fact]
        public void WithNeitherCapabilityTheDimensionsAreZero()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps.Remove("deviceScreenSize");
            driver.Caps.Remove("viewportRect");

            AndroidMetadata metadata = Build(driver);

            Assert.Equal(0, metadata.DeviceScreenWidth());
            Assert.Equal(0, metadata.DeviceScreenHeight());
        }

        [Theory]
        [InlineData("1080")]        // no separator
        [InlineData("wide x tall")] // not numbers
        [InlineData("   ")]
        public void AnUnreadableDeviceScreenSizeFallsBackToTheViewport(string value)
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["deviceScreenSize"] = value;

            Assert.Equal(1080, Build(driver).DeviceScreenWidth());
        }

        [Fact]
        public void AHeightThatIsNotANumberIsRejectedToo()
        {
            StubMobileDriver driver = StubMobileDriver.Android();
            driver.Caps["deviceScreenSize"] = "1080xtall";

            Assert.Equal(1080, Build(driver).DeviceScreenWidth());
        }

        [Fact]
        public void TheScaleFactorIsOneOnAndroid()
        {
            Assert.Equal(1, Build(StubMobileDriver.Android()).ScaleFactor());
        }

        [Fact]
        public void TheViewportRectIsReadOncePerSession()
        {
            // Cached so a sheet with many steps does not re-read it per step.
            Cache<string, object?> cache = new Cache<string, object?>();
            StubMobileDriver driver = StubMobileDriver.Android();
            AndroidMetadata metadata = new AndroidMetadata(driver, new ScreenshotOptions(), cache);

            metadata.StatBarHeight();

            Assert.True(cache.Has("viewportRect_" + driver.SessionId));
        }
    }

    public class IosMetadataTests : CoreTestBase
    {
        private static IosMetadata Build(StubMobileDriver driver, ScreenshotOptions? options = null,
            Cache<string, object?>? cache = null) =>
            new IosMetadata(driver, options ?? new ScreenshotOptions(),
                cache ?? new Cache<string, object?>());

        [Fact]
        public void TheOsNameIsIos()
        {
            Assert.Equal("iOS", Build(StubMobileDriver.Ios()).OsName());
        }

        [Fact]
        public void TheDeviceNameFallsBackFromSuppliedToCapability()
        {
            StubMobileDriver driver = StubMobileDriver.Ios();

            Assert.Equal("iPhone 14",
                Build(driver, new ScreenshotOptions { DeviceName = "iPhone 14" }).DeviceName());
            Assert.Equal("iPhone X", Build(driver).DeviceName());

            driver.Caps.Remove("deviceName");
            driver.Caps["device"] = "iPhone 13";
            Assert.Equal("iPhone 13", Build(driver).DeviceName());

            driver.Caps.Remove("device");
            Assert.Null(Build(driver).DeviceName());
        }

        [Fact]
        public void DimensionsComeFromTheStaticDeviceTableBecauseIosDoesNotReportThem()
        {
            // "iphone x" is in the shipped table; this is the primary source on iOS, not a
            // fallback.
            IosMetadata metadata = Build(StubMobileDriver.Ios("iPhone X"));

            Assert.Equal(1125, metadata.DeviceScreenWidth());
            Assert.Equal(2436, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void SuppliedDimensionsWinOverTheTable()
        {
            IosMetadata metadata = Build(StubMobileDriver.Ios(),
                new ScreenshotOptions { ScreenWidth = 750, ScreenHeight = 1334 });

            Assert.Equal(750, metadata.DeviceScreenWidth());
            Assert.Equal(1334, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void TheStatusBarComesFromTheTableScaledByThePixelRatio()
        {
            // The table stores points; the screenshot the CLI diffs is in pixels.
            int statusBar = DeviceRegistry.Value("statusBarHeight", "iphone x");
            int pixelRatio = DeviceRegistry.Value("pixelRatio", "iphone x");

            Assert.Equal(statusBar * pixelRatio, Build(StubMobileDriver.Ios("iPhone X")).StatBarHeight());
        }

        [Fact]
        public void AnUnknownDeviceFallsBackToTheViewportRect()
        {
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.ScriptReplies.Add(("mobile: viewportRect",
                "{\"top\":47,\"left\":0,\"width\":1179,\"height\":2509}"));

            IosMetadata metadata = Build(driver);

            Assert.Equal(47, metadata.StatBarHeight());
            Assert.Equal(1179, metadata.DeviceScreenWidth());
            Assert.Equal(2509 + 47, metadata.DeviceScreenHeight());
        }

        [Fact]
        public void AViewportRectCapabilityIsPreferredOverAScriptCall()
        {
            // Saves a round trip on a session that already exposes it, and works on one that
            // cannot run scripts at all.
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.Caps["viewportRect"] = new Dictionary<string, object?>
            {
                ["top"] = 44,
                ["width"] = 828,
                ["height"] = 1748
            };

            Assert.Equal(44, Build(driver).StatBarHeight());
            Assert.Empty(driver.ExecutedScripts);
        }

        [Fact]
        public void ASessionThatCannotServeTheViewportYieldsZerosRatherThanFailing()
        {
            // Very likely on Tosca: `mobile: viewportRect` is an Appium extension the Mobile engine
            // may not pass through.
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.ScriptError = new InvalidOperationException("unsupported command");
            SetEnv("PERCY_LOGLEVEL", "debug");

            IosMetadata metadata = Build(driver);

            Assert.Equal(0, metadata.StatBarHeight());
            Assert.Equal(0, metadata.DeviceScreenWidth());
            Assert.Equal(0, metadata.DeviceScreenHeight());
            Assert.True(Logged("viewport"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[1,2]")]
        public void AnUnusableViewportResponseIsTreatedAsAbsent(string? reply)
        {
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.ScriptReplies.Add(("mobile: viewportRect", reply));

            Assert.Equal(0, Build(driver).StatBarHeight());
        }

        [Fact]
        public void TheViewportIsAskedForOncePerSessionEvenWhenItFails()
        {
            Cache<string, object?> cache = new Cache<string, object?>();
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.ScriptError = new InvalidOperationException("unsupported command");

            IosMetadata metadata = Build(driver, cache: cache);
            metadata.StatBarHeight();
            metadata.DeviceScreenWidth();

            Assert.Single(driver.ExecutedScripts);
        }

        [Fact]
        public void ThereIsNoNavBarOnIosUnlessTheStepDeclaresOne()
        {
            Assert.Equal(0, Build(StubMobileDriver.Ios()).NavBarHeight());
            Assert.Equal(12, Build(StubMobileDriver.Ios(),
                new ScreenshotOptions { NavBarHeight = 12 }).NavBarHeight());
        }

        [Fact]
        public void TheScaleFactorIsTheRatioOfRealPixelsToLogicalWidth()
        {
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.Caps["viewportRect"] = new Dictionary<string, object?> { ["width"] = 1170 };
            driver.WindowWidth = 390;

            Assert.Equal(3, Build(driver).ScaleFactor());
        }

        [Fact]
        public void TheScaleFactorIsOneWhenItCannotBeComputed()
        {
            StubMobileDriver noViewport = StubMobileDriver.Ios("Some Unlisted Phone");
            Assert.Equal(1, Build(noViewport).ScaleFactor());

            StubMobileDriver zeroWidth = StubMobileDriver.Ios("Some Unlisted Phone");
            zeroWidth.Caps["viewportRect"] = new Dictionary<string, object?> { ["width"] = 0 };
            Assert.Equal(1, Build(zeroWidth).ScaleFactor());

            StubMobileDriver noWindow = StubMobileDriver.Ios("Some Unlisted Phone");
            noWindow.Caps["viewportRect"] = new Dictionary<string, object?> { ["width"] = 1170 };
            noWindow.WindowWidth = 0;
            Assert.Equal(1, Build(noWindow).ScaleFactor());

            // A logical width wider than the real one would floor to 0, which would zero out every
            // region coordinate.
            StubMobileDriver downscaled = StubMobileDriver.Ios("Some Unlisted Phone");
            downscaled.Caps["viewportRect"] = new Dictionary<string, object?> { ["width"] = 100 };
            downscaled.WindowWidth = 390;
            Assert.Equal(1, Build(downscaled).ScaleFactor());
        }

        [Fact]
        public void AThrowingWindowWidthDoesNotTakeTheSnapshotDown()
        {
            StubMobileDriver driver = StubMobileDriver.Ios("Some Unlisted Phone");
            driver.Caps["viewportRect"] = new Dictionary<string, object?> { ["width"] = 1170 };
            driver.WindowWidthError = new InvalidOperationException("no window");
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Equal(1, Build(driver).ScaleFactor());
            Assert.True(Logged("scale factor"));
        }
    }

    public class DeviceRegistryTests : CoreTestBase
    {
        [Fact]
        public void KnownDevicesResolveCaseInsensitively()
        {
            Assert.Equal(1125, DeviceRegistry.Value("screenWidth", "iPhone X"));
            Assert.Equal(1125, DeviceRegistry.Value("screenWidth", "  IPHONE X  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Some Unlisted Phone")]
        public void AnUnknownDeviceYieldsZeroSoCallersCanFallBack(string? device)
        {
            Assert.Equal(0, DeviceRegistry.Value("screenWidth", device));
        }

        [Fact]
        public void AnUnknownDimensionYieldsZero()
        {
            Assert.Equal(0, DeviceRegistry.Value("nonexistentKey", "iPhone X"));
        }

        [Fact]
        public void TheTableIsReadOncePerProcess()
        {
            DeviceRegistry.Reset();
            Assert.Equal(1125, DeviceRegistry.Value("screenWidth", "iPhone X"));
            Assert.Equal(1125, DeviceRegistry.Value("screenWidth", "iPhone X"));
        }

        [Fact]
        public void AMissingResourceDegradesToDeviceUnknownRatherThanFailingEverySnapshot()
        {
            DeviceRegistry.Reset();
            DeviceRegistry.ResourceLoader = () => null;
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Equal(0, DeviceRegistry.Value("screenWidth", "iPhone X"));
            Assert.True(Logged("device list"));
        }

        [Fact]
        public void AnUnreadableResourceAlsoDegradesToDeviceUnknown()
        {
            DeviceRegistry.Reset();
            DeviceRegistry.ResourceLoader = () => throw new IOException("resource locked");
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Equal(0, DeviceRegistry.Value("screenWidth", "iPhone X"));
            Assert.True(Logged("Could not read the embedded device list"));
        }

        [Fact]
        public void AResourceThatIsNotJsonDegradesToDeviceUnknown()
        {
            DeviceRegistry.Reset();
            DeviceRegistry.ResourceLoader = () =>
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not json"));

            Assert.Equal(0, DeviceRegistry.Value("screenWidth", "iPhone X"));
        }

        [Fact]
        public void ADimensionThatIsNotANumberIsTreatedAsAbsent()
        {
            DeviceRegistry.Reset();
            DeviceRegistry.ResourceLoader = () => new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes("{\"iphone x\":{\"screenWidth\":\"1125\"}}"));

            Assert.Equal(0, DeviceRegistry.Value("screenWidth", "iPhone X"));
        }

        [Fact]
        public void ADimensionTooLargeForAnIntIsTreatedAsAbsent()
        {
            DeviceRegistry.Reset();
            DeviceRegistry.ResourceLoader = () => new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes("{\"iphone x\":{\"screenWidth\":99999999999}}"));

            Assert.Equal(0, DeviceRegistry.Value("screenWidth", "iPhone X"));
        }

        [Fact]
        public void TheEmbeddedResourceIsActuallyShipped()
        {
            // A rename of the project or the resources folder silently changes this name and would
            // leave every iOS device unrecognized; the assertion is here to make that a test
            // failure rather than a wrong-sized tag.
            Assert.NotNull(typeof(DeviceRegistry).Assembly
                .GetManifestResourceStream(DeviceRegistry.ResourceName));
        }
    }
}
