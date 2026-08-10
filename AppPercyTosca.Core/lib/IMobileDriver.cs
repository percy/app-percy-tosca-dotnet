namespace AppPercyTosca.Core
{
    /// Everything the Core needs from the device session, with no Tosca types in the signature.
    ///
    /// Implementations should throw <see cref="PercyException"/> from members the session genuinely
    /// cannot serve, rather than returning empty values.
    public interface IMobileDriver
    {
        /// Unique id for the session; used only as a cache key.
        string SessionId { get; }

        /// The automation server, e.g. https://hub-cloud.browserstack.com/wd/hub.
        string? Host { get; }

        /// "Android" or "iOS". Drives which metadata implementation is used.
        string? PlatformName { get; }

        /// Session capabilities, flattened to a dictionary.
        IReadOnlyDictionary<string, object?> Capabilities { get; }

        /// Current device orientation ("portrait"/"landscape"), or null if unavailable.
        string? Orientation { get; }

        /// The current screen as a base64-encoded PNG.
        string GetScreenshotBase64();

        /// Runs a raw automation script, or null when the session will not run it. Carries the
        /// <c>browserstack_executor:</c> commands and iOS <c>mobile: viewportRect</c>.
        string? ExecuteScript(string script);

        /// The logical window width; on iOS, the real width over this is the scale factor.
        int WindowWidth { get; }
    }
}
