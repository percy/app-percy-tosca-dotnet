using AppPercyTosca.Core;

namespace AppPercyTosca.Core.Tests
{
    /// <summary>
    /// In-memory <see cref="IMobileDriver"/> standing in for a Tosca mobile session. Every member
    /// is settable so a test can describe a session that lacks a capability, or one whose driver
    /// throws, without needing a device.
    /// </summary>
    public class StubMobileDriver : IMobileDriver
    {
        private string _sessionId = "session-1";

        public string SessionId
        {
            get => SessionIdError != null ? throw SessionIdError : _sessionId;
            set => _sessionId = value;
        }
        public string? Host { get; set; }
        public string? PlatformName { get; set; } = "Android";
        public string? Orientation { get; set; }
        private int _windowWidth = 100;

        public int WindowWidth
        {
            get => WindowWidthError != null ? throw WindowWidthError : _windowWidth;
            set => _windowWidth = value;
        }

        public Dictionary<string, object?> Caps { get; set; } = new Dictionary<string, object?>();
        public IReadOnlyDictionary<string, object?> Capabilities => Caps;

        public string Screenshot { get; set; } = ValidPngBase64;

        /// <summary>Defaults to true so existing scripting tests read naturally; a Tosca session
        /// sets it false.</summary>
        public bool CanExecuteScript { get; set; } = true;

        /// <summary>Answers <see cref="ExecuteScript"/>; keyed by a fragment of the script.</summary>
        public List<(string Match, string? Reply)> ScriptReplies { get; } =
            new List<(string, string?)>();

        public List<string> ExecutedScripts { get; } = new List<string>();

        public Dictionary<string, ElementRect?> ElementsByXPath { get; } =
            new Dictionary<string, ElementRect?>();
        public Dictionary<string, ElementRect?> ElementsByAccessibilityId { get; } =
            new Dictionary<string, ElementRect?>();

        /// <summary>Set to make the corresponding member throw, standing in for a session that
        /// cannot serve it.</summary>
        public Exception? ScreenshotError { get; set; }
        public Exception? ScriptError { get; set; }
        public Exception? FindElementError { get; set; }
        public Exception? WindowWidthError { get; set; }
        public Exception? SessionIdError { get; set; }

        /// <summary>A 1x1 transparent PNG — the smallest thing that decodes as real image bytes.</summary>
        public const string ValidPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        public string GetScreenshotBase64()
        {
            if (ScreenshotError != null) throw ScreenshotError;
            return Screenshot;
        }

        public string? ExecuteScript(string script)
        {
            ExecutedScripts.Add(script);
            if (ScriptError != null) throw ScriptError;
            foreach ((string match, string? reply) in ScriptReplies)
            {
                if (script.Contains(match, StringComparison.Ordinal)) return reply;
            }
            return null;
        }

        public ElementRect? FindElementByXPath(string xpath)
        {
            if (FindElementError != null) throw FindElementError;
            return ElementsByXPath.TryGetValue(xpath, out ElementRect? rect) ? rect : null;
        }

        public ElementRect? FindElementByAccessibilityId(string accessibilityId)
        {
            if (FindElementError != null) throw FindElementError;
            return ElementsByAccessibilityId.TryGetValue(accessibilityId, out ElementRect? rect)
                ? rect
                : null;
        }

        /// <summary>An Android session with the capabilities the metadata layer expects.</summary>
        public static StubMobileDriver Android(int statusBar = 60, int navBar = 40)
        {
            return new StubMobileDriver
            {
                PlatformName = "Android",
                Caps = new Dictionary<string, object?>
                {
                    ["platformName"] = "Android",
                    ["platformVersion"] = "13",
                    ["device"] = "Samsung Galaxy S22",
                    ["deviceScreenSize"] = "1080x2340",
                    ["viewportRect"] = new Dictionary<string, object?>
                    {
                        ["top"] = statusBar,
                        ["left"] = 0,
                        ["width"] = 1080,
                        ["height"] = 2340 - statusBar - navBar
                    }
                }
            };
        }

        /// <summary>An iOS session, which reports no screen size of its own.</summary>
        public static StubMobileDriver Ios(string deviceName = "iPhone X")
        {
            return new StubMobileDriver
            {
                PlatformName = "iOS",
                Caps = new Dictionary<string, object?>
                {
                    ["platformName"] = "iOS",
                    ["platformVersion"] = "16.2",
                    ["deviceName"] = deviceName
                }
            };
        }
    }
}
