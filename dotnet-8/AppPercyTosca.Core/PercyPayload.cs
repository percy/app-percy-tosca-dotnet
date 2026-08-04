using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Payload serialization for Percy CLI requests.
    /// </summary>
    public static class PercyPayload
    {
        /// <summary>
        /// Options used for every CLI request body. Nulls are written, not dropped: the other App
        /// Percy SDKs build their bodies with Newtonsoft, which emits null members, so every
        /// endpoint here already expects e.g. `"sha": null` on a local tile. Omitting them would
        /// be an untested deviation from a wire format the CLI is known to accept. Options a step
        /// left unset are kept out of the payload by the callers instead
        /// (see <see cref="ToscaOptions.BuildAutomateOptions"/>).
        /// </summary>
        public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions();

        /// <summary>
        /// Serializes a payload to a JSON string.
        /// When <paramref name="alreadyJson"/> is true the payload is passed through
        /// (null becomes an empty string); otherwise it is JSON serialized.
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
