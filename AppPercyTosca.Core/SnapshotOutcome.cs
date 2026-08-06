using System.Text.Json;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// What an AppPercyScreenshot step reports back to Tosca.
    ///
    /// This lives in the Core rather than the shim for one reason: the rule it encodes is easy to
    /// "simplify" away. The step passes whether or not a snapshot was recorded — a visual check that
    /// could not run is not a functional regression, and failing would stop the rest of the sheet —
    /// but it must never *claim* one was recorded. A green step reading "Snapshot Taken!" when nothing
    /// reached Percy is worse than an outright failure, because nobody goes looking. Keeping it here
    /// means a test holds that rule in place.
    /// </summary>
    public static class SnapshotOutcome
    {
        /// <summary>Reported when a snapshot was accepted by the CLI.</summary>
        public const string Taken = "Snapshot Taken!";

        /// <summary>Reported when the CLI is not running, or is too old to serve App Percy.</summary>
        public const string PercyNotRunning = "Percy is not running, so no snapshot was taken";

        /// <summary>
        /// Turns the CLI's answer into the step's message.
        ///
        /// Judged on the response existing, not on any member of it. A successful /percy/comparison
        /// reply is `{success, link}` and often carries no `data`, so an earlier version that looked for
        /// `data` reported perfectly good snapshots as unrecorded — the worst kind of wrong, since it
        /// sends someone hunting for a problem they do not have.
        ///
        /// A null means the snapshot did not reach Percy: either it was disabled for the session, or
        /// capture failed and errors are being ignored. Both have already been explained in the log, so
        /// this only has to avoid claiming success; naming one of the two would mean guessing which.
        /// </summary>
        public static string Describe(JsonElement? response, string snapshotName)
        {
            if (response == null)
            {
                return $"No snapshot was recorded for \"{snapshotName}\" — see the Percy log for why";
            }

            // The comparison URL when the CLI supplies one: it turns a passing step into something
            // someone can click.
            string? link = Json.PropertyAsString(response, "link");
            return string.IsNullOrWhiteSpace(link) ? Taken : $"{Taken} {link}";
        }
    }
}
