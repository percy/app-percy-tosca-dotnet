namespace AppPercyTosca.Core
{
    /// <summary>
    /// The baseline App Percy capture path: screenshot the device locally, write it to a tile on
    /// disk, and post it to the CLI with the device tag and any ignore/consider regions.
    /// </summary>
    public class GenericProvider
    {
        protected readonly IMobileDriver Driver;
        protected readonly PercyClient Client;
        protected readonly Cache<string, object?> SessionCache;

        /// <summary>Set to the App Automate session URL when one is known; unused otherwise.</summary>
        private string? _debugUrl;

        /// <summary>
        /// Resolved for the current snapshot in <see cref="Screenshot"/>. Subclasses read it while
        /// building their capture request, which happens after that assignment.
        /// </summary>
        protected Metadata Metadata { get; private set; } = null!;

        public GenericProvider(IMobileDriver driver, PercyClient client, Cache<string, object?> sessionCache)
        {
            Driver = driver;
            Client = client;
            SessionCache = sessionCache;
        }

        public void SetDebugUrl(string? debugUrl) => _debugUrl = debugUrl;

        /// <summary>
        /// Captures and posts one snapshot. <paramref name="platformVersion"/> lets App Automate
        /// pass the OS version its executor reported, which is more reliable than the capability.
        /// </summary>
        public virtual System.Text.Json.JsonElement? Screenshot(
            string name, ScreenshotOptions options, string? platformVersion = null)
        {
            if (platformVersion != null && string.IsNullOrWhiteSpace(options.PlatformVersion))
            {
                options.PlatformVersion = platformVersion;
            }

            Metadata = MetadataResolver.Resolve(Driver, options, SessionCache);

            // Capture first, then measure. The screenshot is the most reliable statement of how big
            // the screen is — often the only one, since a Tosca mobile session reports no size — and
            // it costs nothing to read. Doing it in this order means both the comparison tag and
            // custom-region validation below get real dimensions instead of zeros, rather than making
            // the user type them onto the module.
            List<Tile> tiles = CaptureTiles(options);
            MeasureScreenFromTiles(tiles);

            Dictionary<string, object?> tag = Metadata.GetTag();
            List<Dictionary<string, object?>> ignored = FindRegions(
                options.IgnoreRegionXpaths,
                options.IgnoreRegionAccessibilityIds,
                options.CustomIgnoreRegions);
            List<Dictionary<string, object?>> considered = FindRegions(
                options.ConsiderRegionXpaths,
                options.ConsiderRegionAccessibilityIds,
                options.CustomConsiderRegions);

            return Client.PostScreenshot(
                name,
                tag,
                tiles,
                _debugUrl,
                new Dictionary<string, object?> { ["ignoreElementsData"] = ignored },
                new Dictionary<string, object?> { ["considerElementsData"] = considered },
                options);
        }

        /// <summary>
        /// Captures the visible screen as a single tile written under the temp directory. Full-page
        /// capture needs the App Automate executor, so it is announced as unavailable here rather
        /// than silently producing a single-screen snapshot the user believes is full page.
        /// </summary>
        public virtual List<Tile> CaptureTiles(ScreenshotOptions options)
        {
            if (options.FullPage)
            {
                Utils.Log("Full page screenshot is only supported on App Automate." +
                    " Falling back to single page screenshot.");
            }

            string localFilePath = WriteTile(Driver.GetScreenshotBase64());
            return new List<Tile>
            {
                new Tile(localFilePath, Metadata.StatBarHeight(), Metadata.NavBarHeight(),
                    0, 0, options.FullScreen)
            };
        }

        /// <summary>
        /// Tells the metadata layer how big the screen is, measured from the tile just captured.
        ///
        /// Only ever a fallback: anything the step declared or the session reported still wins. This
        /// covers the case that is otherwise unserviceable — a session with no screen-size capability
        /// and a device absent from the static table — which on Tosca is the normal case rather than
        /// an edge one.
        ///
        /// Skipped for a remotely-uploaded tile, which has a content hash and no local file; that path
        /// has real device capabilities to read instead.
        /// </summary>
        private void MeasureScreenFromTiles(List<Tile> tiles)
        {
            string? path = tiles.FirstOrDefault(t => t.LocalFilePath != null)?.LocalFilePath;
            if (path == null) return;

            (int Width, int Height)? size = PngSize.TryReadFile(path);
            if (size == null) return;

            Metadata.UseFallbackScreenSize(size.Value.Width, size.Value.Height);
            Utils.Log($"Measured the device screen as {size.Value.Width}x{size.Value.Height} from the " +
                "screenshot.", "debug");
        }

        /// <summary>
        /// Decodes a base64 screenshot to a PNG the CLI can read. The CLI reads it from disk by
        /// path, so the file deliberately outlives this call and is cleaned up by the OS temp
        /// sweep — the same contract the other App Percy SDKs use.
        /// </summary>
        private static string WriteTile(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new PercyException(
                    "The session returned an empty screenshot. Check that the device is " +
                    "connected and the app is in the foreground.");
            }

            string dir = Env.TempDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"percy-{Guid.NewGuid()}.png");
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
            return path;
        }

        /// <summary>
        /// Resolves every declared region to device-pixel coordinates. A locator that does not
        /// match is skipped with a log line rather than failing the snapshot: an ignore region for
        /// an element that is legitimately absent on this screen is a normal thing for a Tosca
        /// sheet to declare once and reuse.
        /// </summary>
        public List<Dictionary<string, object?>> FindRegions(
            List<string> xpaths, List<string> accessibilityIds, List<Region> customRegions)
        {
            List<Dictionary<string, object?>> regions = new List<Dictionary<string, object?>>();
            AddRegionsByLocator(regions, xpaths, "xpath", Driver.FindElementByXPath);
            AddRegionsByLocator(regions, accessibilityIds, "id", Driver.FindElementByAccessibilityId);
            AddCustomRegions(regions, customRegions);
            return regions;
        }

        private void AddRegionsByLocator(
            List<Dictionary<string, object?>> regions,
            List<string> locators,
            string kind,
            Func<string, ElementRect?> resolve)
        {
            foreach (string locator in locators)
            {
                try
                {
                    ElementRect? element = resolve(locator);
                    if (element == null)
                    {
                        Utils.Log($"Element with {kind}: {locator} not found. Ignoring this {kind}.");
                        continue;
                    }
                    regions.Add(RegionPayload($"{kind}: {locator}", element));
                }
                catch (Exception e)
                {
                    Utils.Log($"Element with {kind}: {locator} not found. Ignoring this {kind}.");
                    Utils.Log(e.ToString(), "debug");
                }
            }
        }

        private void AddCustomRegions(List<Dictionary<string, object?>> regions, List<Region> customRegions)
        {
            if (customRegions.Count == 0) return;

            int width = Metadata.DeviceScreenWidth();
            int height = Metadata.DeviceScreenHeight();

            // With no known screen size there is nothing to validate against, and IsValid would
            // reject every region for exceeding a zero-sized screen. Passing them through unchecked
            // respects what the user actually asked for; discarding them would silently drop the only
            // region type available on a Tosca session, blaming the region rather than the missing
            // dimensions. GetTag has already warned about those.
            bool canValidate = width > 0 && height > 0;
            if (!canValidate)
            {
                Utils.Log($"Passing {customRegions.Count} custom region(s) through unchecked: the " +
                    "device screen size is unknown, so they cannot be validated against it.", "debug");
            }

            for (int index = 0; index < customRegions.Count; index++)
            {
                Region region = customRegions[index];
                if (canValidate && !region.IsValid(height, width))
                {
                    Utils.Log($"Values passed in custom region at index:- {index} is not valid");
                    continue;
                }
                regions.Add(new Dictionary<string, object?>
                {
                    ["selector"] = $"custom region {index}",
                    ["co_ordinates"] = new Dictionary<string, object?>
                    {
                        ["top"] = region.Top,
                        ["bottom"] = region.Bottom,
                        ["left"] = region.Left,
                        ["right"] = region.Right
                    }
                });
            }
        }

        /// <summary>
        /// Converts an element rect to the region payload. Coordinates are scaled because the
        /// session reports points while the screenshot the CLI diffs is in pixels.
        /// </summary>
        private Dictionary<string, object?> RegionPayload(string selector, ElementRect element)
        {
            int scale = Metadata.ScaleFactor();
            return new Dictionary<string, object?>
            {
                ["selector"] = selector,
                ["co_ordinates"] = new Dictionary<string, object?>
                {
                    ["top"] = element.Y * scale,
                    ["bottom"] = (element.Y + element.Height) * scale,
                    ["left"] = element.X * scale,
                    ["right"] = (element.X + element.Width) * scale
                }
            };
        }
    }
}
