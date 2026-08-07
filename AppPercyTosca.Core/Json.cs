using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Helpers for the CLI's JSON. System.Text.Json only, no Newtonsoft: the DLL Tosca loads must not
    /// carry a third-party dependency that could clash with one already in Commander.
    /// </summary>
    public static class Json
    {
        /// <summary>Parses into a <see cref="JsonElement"/>, or null when empty or not valid JSON.</summary>
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

        /// <summary>A property, or null when absent or the element is not an object.</summary>
        public static JsonElement? Property(JsonElement? element, string name)
        {
            if (element == null || element.Value.ValueKind != JsonValueKind.Object) return null;
            return element.Value.TryGetProperty(name, out JsonElement value) ? value : null;
        }

        /// <summary>Numbers and booleans render rather than reject, so an unquoted `build.id` works.</summary>
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

        /// <summary>For the CLI's `success` flag, which some endpoints stringify.</summary>
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

        /// <summary>Converts to plain CLR objects: object to dictionary, array to list, primitives as-is.</summary>
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
                // Undefined and any kind a future runtime adds land here too; null is the only
                // sensible reading of "no value".
                default:
                    return null;
            }
        }
    }
}
