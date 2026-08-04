namespace AppPercyTosca.Core
{
    /// <summary>
    /// One captured screen handed to the CLI. Either <see cref="LocalFilePath"/> (the tile was
    /// written to disk locally) or <see cref="Sha"/> (App Automate already uploaded it and
    /// returned a content hash) is set — never both.
    /// </summary>
    public class Tile
    {
        public string? LocalFilePath { get; }
        public int StatusBarHeight { get; }
        public int NavBarHeight { get; }
        public int HeaderHeight { get; }
        public int FooterHeight { get; }
        public bool FullScreen { get; }
        public string? Sha { get; }

        public Tile(
            string? localFilePath,
            int statusBarHeight,
            int navBarHeight,
            int headerHeight,
            int footerHeight,
            bool fullScreen,
            string? sha = null)
        {
            LocalFilePath = localFilePath;
            StatusBarHeight = statusBarHeight;
            NavBarHeight = navBarHeight;
            HeaderHeight = headerHeight;
            FooterHeight = footerHeight;
            FullScreen = fullScreen;
            Sha = sha;
        }

        /// <summary>
        /// Shapes the tile the way the CLI's /percy/comparison endpoint expects. The key names
        /// are the wire contract and are deliberately not camel-cased consistently ("fullscreen").
        /// </summary>
        public Dictionary<string, object?> ToPayload() => new Dictionary<string, object?>
        {
            ["filepath"] = LocalFilePath,
            ["statusBarHeight"] = StatusBarHeight,
            ["navBarHeight"] = NavBarHeight,
            ["headerHeight"] = HeaderHeight,
            ["footerHeight"] = FooterHeight,
            ["fullscreen"] = FullScreen,
            ["sha"] = Sha
        };

        public static List<Dictionary<string, object?>> ToPayload(IEnumerable<Tile> tiles) =>
            tiles.Select(t => t.ToPayload()).ToList();
    }
}
