using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Percy on Automate: instead of capturing locally, hand the CLI the session details and let it
    /// reconnect and capture server-side. Selected when the CLI's healthcheck reports session type
    /// "automate". Nothing here reads the device, so it works on a Tosca mobile session that
    /// exposes little more than its session id and hub URL.
    /// </summary>
    public class PercyOnAutomate
    {
        private readonly IMobileDriver _driver;
        private readonly PercyClient _client;
        private readonly bool _isPercyEnabled;

        /// <summary>
        /// Element-list keys the user passes; they are replaced with the resolved element ids the
        /// CLI expects, since it re-resolves the elements against the session itself.
        /// </summary>
        internal const string IgnoreElementKey = "ignore_region_appium_elements";
        internal const string ConsiderElementKey = "consider_region_appium_elements";

        public PercyOnAutomate(IMobileDriver driver, PercyClient client)
        {
            _driver = driver;
            _client = client;
            _isPercyEnabled = client.Healthcheck();
        }

        /// <summary>
        /// Posts one Percy on Automate snapshot. Options are passed through largely untouched — the
        /// CLI owns their schema, so an option added there needs no change here.
        /// </summary>
        public JsonElement? Screenshot(string name, Dictionary<string, object?>? options = null)
        {
            if (!_isPercyEnabled) return null;

            try
            {
                Dictionary<string, object?> userOptions = options == null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(options);

                ResolveElementIds(userOptions, IgnoreElementKey, "ignore_region_elements");
                ResolveElementIds(userOptions, ConsiderElementKey, "consider_region_elements");

                return Json.Property(_client.PostAutomateScreenshot(
                    name,
                    _driver.SessionId,
                    _driver.Host?.TrimEnd('/'),
                    _driver.Capabilities,
                    userOptions), "data");
            }
            catch (Exception error)
            {
                Utils.Log($"Could not take Percy Screenshot \"{name}\"");
                Utils.Log(error.ToString(), "debug");
                return null;
            }
        }

        /// <summary>
        /// Replaces a list of locators under <paramref name="sourceKey"/> with the session's own
        /// element ids under <paramref name="targetKey"/>. Locators that do not resolve are dropped
        /// with a log line, matching the generic provider's treatment of absent regions.
        /// </summary>
        private void ResolveElementIds(
            Dictionary<string, object?> options, string sourceKey, string targetKey)
        {
            if (!options.TryGetValue(sourceKey, out object? raw)) return;
            options.Remove(sourceKey);

            if (raw is not System.Collections.IEnumerable locators || raw is string) return;

            List<string> ids = new List<string>();
            int requested = 0;
            foreach (object? locator in locators)
            {
                requested++;
                string? xpath = locator as string;
                if (string.IsNullOrWhiteSpace(xpath)) continue;
                try
                {
                    ElementRect? element = _driver.FindElementByXPath(xpath);
                    if (element?.Id != null)
                    {
                        ids.Add(element.Id);
                    }
                    else
                    {
                        Utils.Log($"Element with xpath: {xpath} not found. Ignoring this xpath.");
                    }
                }
                catch (Exception e)
                {
                    Utils.Log($"Element with xpath: {xpath} not found. Ignoring this xpath.");
                    Utils.Log(e.ToString(), "debug");
                }
            }
            if (ids.Count == 0 && requested > 0)
            {
                // Nothing resolved, so sending `targetKey: []` would tell the CLI "no regions" and the
                // user's request would vanish silently. Omitting the key at least leaves any
                // project-level configuration in place, and says why.
                Utils.Log($"None of the {requested} entr{(requested == 1 ? "y" : "ies")} under " +
                    $"'{sourceKey}' could be resolved to an element, so no {targetKey} are being sent. " +
                    "On Tosca this key needs element objects the mobile engine does not expose — use " +
                    "IgnoreRegionXpaths, or pass pre-resolved ids as 'ignore_region_elements'.", "warn");
                return;
            }
            options[targetKey] = ids;
        }
    }
}
