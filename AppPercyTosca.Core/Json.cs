using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Small helpers for reading the CLI's JSON responses. The Core deliberately depends on
    /// System.Text.Json only (no Newtonsoft) so the assembly the Tosca shim loads carries no
    /// third-party dependency that could clash with one already loaded into Tosca Commander.
    /// </summary>
    public static class Json
    {
        /// <summary>
        /// Parses <paramref name="content"/> into a <see cref="JsonElement"/>, or returns null
        /// when the body is empty or not valid JSON.
        /// </summary>
        public static JsonElement? TryParse(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(content);
                // Clone(): the JsonElement is only valid while its JsonDocument is alive.
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a property from a JSON object, or null when the element is not an object or
        /// the property is absent.
        /// </summary>
        public static JsonElement? Property(JsonElement? element, string name)
        {
            if (element == null || element.Value.ValueKind != JsonValueKind.Object) return null;
            return element.Value.TryGetProperty(name, out JsonElement value) ? value : null;
        }

        /// <summary>
        /// Reads a property as a string. Numbers and booleans are rendered rather than rejected,
        /// so a `build.id` that arrives unquoted still yields a usable value.
        /// </summary>
        public static string? PropertyAsString(JsonElement? element, string name)
        {
            JsonElement? value = Property(element, name);
            if (value == null) return null;
            return value.Value.ValueKind switch
            {
                JsonValueKind.String => value.Value.GetString(),
                JsonValueKind.Null => null,
                _ => value.Value.GetRawText()
            };
        }

        /// <summary>
        /// True only when the named property is present and is boolean true or the string "true"
        /// (case-insensitive). Used for the CLI's `success` flag, which some endpoints stringify.
        /// </summary>
        public static bool IsTrue(JsonElement? element, string name)
        {
            JsonElement? value = Property(element, name);
            if (value == null) return false;
            return value.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => string.Equals(value.Value.GetString(), "true",
                    StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        /// <summary>
        /// Recursively converts a <see cref="JsonElement"/> into plain CLR objects:
        /// object -> Dictionary&lt;string, object?&gt;, array -> List&lt;object?&gt;, primitives as-is.
        /// </summary>
        public static object? ToObject(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, object?> dict = new Dictionary<string, object?>();
                    foreach (JsonProperty prop in element.EnumerateObject())
                        dict[prop.Name] = ToObject(prop.Value);
                    return dict;
                case JsonValueKind.Array:
                    List<object?> list = new List<object?>();
                    foreach (JsonElement item in element.EnumerateArray())
                        list.Add(ToObject(item));
                    return list;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return element.TryGetInt64(out long longVal) ? longVal : (object)element.GetDouble();
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Null:
                // Undefined (a default JsonElement) and any kind a future runtime adds land here
                // too. Null is the only sensible reading of "no value", and sharing the arm keeps
                // this from being an untestable branch.
                default:
                    return null;
            }
        }
    }
}
