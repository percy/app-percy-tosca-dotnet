using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// Payload serialization for Percy CLI requests.
    public static class PercyPayload
    {
        /// Nulls are written, not dropped: the other App Percy SDKs serialize with Newtonsoft, which
        /// emits null members, so the CLI already expects e.g. `"sha": null` on a local tile.
        public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions();

        /// Serializes a payload, or passes it through when <paramref name="alreadyJson"/> is set.
        public static string PayloadParser(object? payload = null, bool alreadyJson = false)
        {
            if (alreadyJson)
            {
                return payload is null ? "" : payload.ToString()!;
            }
            return JsonSerializer.Serialize(payload, SerializerOptions);
        }
    }
}
