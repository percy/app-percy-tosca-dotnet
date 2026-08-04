using System.Globalization;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Typed reads over a session's capability bag. Capabilities arrive from the automation server
    /// as loosely-typed JSON, so the same key can show up as a string on one session and a number
    /// or nested map on another; these helpers coerce rather than reject, and return null on a
    /// genuine miss so callers can fall back.
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

        /// <summary>
        /// Reads a capability as an int, accepting the numeric and string spellings both
        /// protocols produce. Returns null when the value is missing or not a number.
        /// </summary>
        public static int? GetInt(this IReadOnlyDictionary<string, object?> caps, string key)
        {
            return ToInt(caps.Raw(key));
        }

        /// <summary>
        /// Reads a nested capability object (percyOptions, viewportRect, bstack:options, ...).
        /// Returns null when absent or not object-shaped.
        /// </summary>
        public static IReadOnlyDictionary<string, object?>? GetMap(
            this IReadOnlyDictionary<string, object?> caps, string key)
        {
            return AsDictionary(caps.Raw(key));
        }

        /// <summary>
        /// Coerces an arbitrary capability value into an int. Doubles are truncated and strings
        /// parsed with invariant culture — a device reporting "1080" must not depend on the
        /// Tosca workstation's locale.
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

        /// <summary>
        /// True only when the value is explicitly false — boolean false or the string "false".
        /// A missing value is not "false"; Percy defaults to enabled when nothing is declared.
        /// </summary>
        public static bool IsFalse(object? value)
        {
            if (value == null) return false;
            if (value is bool b) return !b;
            if (value is string str) return string.Equals(str, "false", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        /// <summary>
        /// Normalizes the several shapes a nested capability can arrive in — a plain dictionary,
        /// a string-keyed dictionary of concrete type, or a parsed JsonElement.
        /// </summary>
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
                // Non-generic last: Dictionary<string, object> and the concrete map types an
                // automation client hands back all land here. `object?` and `object` are the same
                // type once nullable annotations are erased, so they cannot be separate cases.
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
