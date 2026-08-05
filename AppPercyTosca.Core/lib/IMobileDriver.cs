namespace AppPercyTosca.Core
{
    /// <summary>
    /// Position and size of an on-screen element, in the coordinate space the automation session
    /// reports (i.e. before <c>ScaleFactor</c> is applied).
    /// </summary>
    public class ElementRect
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public ElementRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Everything the Core needs from the device session under test, with no Tosca types in the
    /// signature. The Tosca shim implements this over the Mobile engine's session; tests
    /// implement it directly.
    ///
    /// Implementations should throw <see cref="PercyException"/> from members the underlying
    /// session genuinely cannot serve, rather than returning empty values — the Core distinguishes
    /// "unsupported" from "absent" when deciding whether to fall back.
    /// </summary>
    public interface IMobileDriver
    {
        /// <summary>Unique id for the session; used only as a cache key.</summary>
        string SessionId { get; }

        /// <summary>
        /// The automation server this session talks to, e.g.
        /// https://hub-cloud.browserstack.com/wd/hub. Null when the session is not remote —
        /// which routes capture down the local tile path instead of App Automate's.
        /// </summary>
        string? Host { get; }

        /// <summary>"Android" or "iOS". Drives which metadata implementation is used.</summary>
        string? PlatformName { get; }

        /// <summary>Session capabilities, flattened to a dictionary.</summary>
        IReadOnlyDictionary<string, object?> Capabilities { get; }

        /// <summary>Current device orientation ("portrait"/"landscape"), or null if unavailable.</summary>
        string? Orientation { get; }

        /// <summary>The current screen as a base64-encoded PNG.</summary>
        string GetScreenshotBase64();

        /// <summary>
        /// Whether raw automation commands can be sent at all. False on a Tosca mobile session,
        /// which is why this is asked rather than assumed: App Automate's remote capture is driven
        /// entirely by <c>browserstack_executor</c> commands, so a session that cannot send them
        /// must take the local-capture path even when it is running against an App Automate hub.
        /// </summary>
        bool CanExecuteScript { get; }

        /// <summary>
        /// Runs a raw automation script and returns its result as a string. Used for the
        /// <c>browserstack_executor:</c> commands and iOS <c>mobile: viewportRect</c>. Returns null
        /// when <see cref="CanExecuteScript"/> is false.
        /// </summary>
        string? ExecuteScript(string script);

        /// <summary>Resolves an element by XPath, or null when not found.</summary>
        ElementRect? FindElementByXPath(string xpath);

        /// <summary>Resolves an element by accessibility id, or null when not found.</summary>
        ElementRect? FindElementByAccessibilityId(string accessibilityId);

        /// <summary>
        /// Width of the session's logical window. On iOS the ratio between the real viewport
        /// width and this value is the screen's scale factor.
        /// </summary>
        int WindowWidth { get; }
    }
}
