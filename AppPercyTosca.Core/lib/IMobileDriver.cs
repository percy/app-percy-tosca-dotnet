namespace AppPercyTosca.Core
{
    /// <summary>
    /// Everything the Core needs from the device session, with no Tosca types in the signature.
    ///
    /// Implementations should throw <see cref="PercyException"/> from members the session genuinely
    /// cannot serve, rather than returning empty values.
    /// </summary>
    public interface IMobileDriver
    {
        /// <summary>Unique id for the session; used only as a cache key.</summary>
        string SessionId { get; }

        /// <summary>The automation server, e.g. https://hub-cloud.browserstack.com/wd/hub.</summary>
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
        /// Runs a raw automation script, or null when the session will not run it. Carries the
        /// <c>browserstack_executor:</c> commands and iOS <c>mobile: viewportRect</c>.
        /// </summary>
        string? ExecuteScript(string script);

        /// <summary>The logical window width; on iOS, the real width over this is the scale factor.</summary>
        int WindowWidth { get; }
    }
}
