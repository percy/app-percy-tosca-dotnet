namespace AppPercyTosca.Core
{
    /// Result of a Percy CLI request: the resolved core version (from the
    /// x-percy-core-version header, null when absent) and the response body.
    public class PercyResponse
    {
        public string? Version { get; }
        public string Content { get; }

        public PercyResponse(string? version, string content)
        {
            Version = version;
            Content = content;
        }
    }
}
