using System.Globalization;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Typed reads over a capability bag. The same key arrives as a string on one session and a number
    /// or nested map on another, so these coerce rather than reject, and return null on a real miss.
    /// </summary>
    public static class Capabilities
    {
        public static object? Raw(this IReadOnlyDictionary<string, object?> caps, string key)
        {
            if (caps == null) return null;
            if (caps.TryGetValue(key, out object? value) && value != null) return value;

            // W3C sessions nest vendor capabilities under bstack:options / appium:options rather
            // than flattening them, so a key absent at the top level may still be present there.
            foreach (string container in new[] { "bstack:options", "appium:options", "desired" })
            {
                if (caps.TryGetValue(container, out object? nested) &&
                    AsDictionary(nested) is IReadOnlyDictionary<string, object?> nestedDict &&
                    nestedDict.TryGetValue(key, out object? nestedValue) && nestedValue != null)
                {
                    return nestedValue;
                }
            }
            return null;
        }

        public static string? GetString(this IReadOnlyDictionary<string, object?> caps, string key)
        {
            object? value = caps.Raw(key);
            if (value == null) return null;
            if (value is string str) return str;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>Accepts the numeric and string spellings both protocols produce.</summary>
        public static int? GetInt(this IReadOnlyDictionary<string, object?> caps, string key)
        {
            return ToInt(caps.Raw(key));
        }

        /// <summary>A nested capability object (viewportRect, bstack:options, ...), or null.</summary>
        public static IReadOnlyDictionary<string, object?>? GetMap(
            this IReadOnlyDictionary<string, object?> caps, string key)
        {
            return AsDictionary(caps.Raw(key));
        }

        /// <summary>
        /// Doubles truncate; strings parse with invariant culture, so a device reporting "1080" does
        /// not depend on the workstation's locale.
        /// </summary>
        public static int? ToInt(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case int i:
                    return i;
                case long l:
                    return (int)l;
                case short s:
                    return s;
                case double d:
                    return (int)d;
                case float f:
                    return (int)f;
                case decimal m:
                    return (int)m;
                case bool:
                    return null;
                case string str:
                    return int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                        ? parsed
                        : double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbl)
                            ? (int)dbl
                            : null;
                default:
                    return null;
            }
        }

        /// <summary>Only an explicit false counts: Percy defaults to enabled when nothing is declared.</summary>
        public static bool IsFalse(object? value)
        {
            if (value == null) return false;
            if (value is bool b) return !b;
            if (value is string str) return string.Equals(str, "false", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        /// <summary>Normalizes a dictionary, a concrete string-keyed map, or a parsed JsonElement.</summary>
        public static IReadOnlyDictionary<string, object?>? AsDictionary(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case IReadOnlyDictionary<string, object?> readOnly:
                    return readOnly;
                case IDictionary<string, object?> nullableDict:
                    return new Dictionary<string, object?>(nullableDict);
                case System.Text.Json.JsonElement element:
                    return Json.ToObject(element) as Dictionary<string, object?>;
                // Non-generic last: `object?` and `object` are the same type once annotations are
                // erased, so they cannot be separate cases.
                case System.Collections.IDictionary dictionary:
                    Dictionary<string, object?> converted = new Dictionary<string, object?>();
                    foreach (System.Collections.DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is string key) converted[key] = entry.Value;
                    }
                    return converted;
                default:
                    return null;
            }
        }
    }
}
