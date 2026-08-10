using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// What a step reports back to Tosca. In the Core rather than the shim so a test holds the rule in
    /// place: the step passes whether or not a snapshot was recorded, but must never claim one that
    /// was not. A green step reading "Snapshot Taken!" with nothing in the build is worse than a
    /// failure, because nobody goes looking.
    public static class SnapshotOutcome
    {
        /// Reported when a snapshot was accepted by the CLI.
        public const string Taken = "Snapshot Taken!";

        /// Reported when the CLI is not running, or is too old to serve App Percy.
        public const string PercyNotRunning = "Percy is not running, so no snapshot was taken";

        /// Judged on the response existing, not on any member of it: a successful /percy/comparison
        /// reply is `{success, link}` and often carries no `data`.
        ///
        /// A null means the snapshot did not reach Percy, for a reason already in the log — so this
        /// only has to avoid claiming success rather than guess which reason it was.
        public static string Describe(JsonElement? response, string snapshotName)
        {
            if (response == null)
            {
                return $"No snapshot was recorded for \"{snapshotName}\" — see the Percy log for why";
            }

            // The comparison URL turns a passing step into something someone can click.
            string? link = Json.PropertyAsString(response, "link");
            return string.IsNullOrWhiteSpace(link) ? Taken : $"{Taken} {link}";
        }
    }
}
