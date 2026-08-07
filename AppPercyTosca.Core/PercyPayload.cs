using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Payload serialization for Percy CLI requests.
    /// </summary>
    public static class PercyPayload
    {
        /// <summary>
        /// Nulls are written, not dropped: the other App Percy SDKs serialize with Newtonsoft, which
        /// emits null members, so the CLI already expects e.g. `"sha": null` on a local tile.
        /// </summary>
        public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions();

        /// <summary>
        /// Serializes a payload, or passes it through when <paramref name="alreadyJson"/> is set.
        /// </summary>
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
