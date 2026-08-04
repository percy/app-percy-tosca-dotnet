using AppPercyTosca.Core;

namespace AppPercyTosca
{
    /// <summary>
    /// Where the SDK's log lines go inside Tosca Commander.
    ///
    /// Tosca Commander is a desktop process with no console attached, so <c>Console.WriteLine</c>
    /// goes nowhere. Lines are forwarded to the Percy CLI, which interleaves them with the rest of
    /// the build's output where a user expects to find them, and mirrored to a file so that a failure
    /// to reach the CLI — the very failure the line is most likely reporting — still leaves a record
    /// on disk.
    /// </summary>
    internal static class ToscaLog
    {
        /// <summary>
        /// Typically C:\Users\&lt;user&gt;\AppData\Local\Temp\percy.txt. Deliberately the same
        /// filename the HTML SDK uses, so anyone who has debugged that one knows where to look.
        /// </summary>
        internal static readonly string LogPath = Path.Combine(Path.GetTempPath(), "percy.txt");

        private static readonly object FileLock = new object();

        /// <summary>
        /// Installed as <see cref="Utils.LogSink"/>. Never throws: the Core falls back to stdout if
        /// this fails, and a logging failure must not fail a test step.
        /// </summary>
        internal static void Write(string message, string level)
        {
            string label = level == "info" ? "percy" : "percy:tosca";
            string labelled = $"[{label}] {message}";

            WriteToFile(labelled);
            ForwardToCli(labelled, level);
        }

        private static void WriteToFile(string message)
        {
            try
            {
                // Locked because Tosca can run test steps concurrently, and two appends racing on
                // the same handle lose lines.
                lock (FileLock)
                {
                    File.AppendAllText(LogPath, message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Nowhere left to report this; the CLI forward below is the remaining channel.
            }
        }

        private static void ForwardToCli(string message, string level)
        {
            try
            {
                AppPercyScreenshot.CliClient.PostLog(message, level);
            }
            catch (Exception)
            {
                // Expected whenever Percy is not running, which is exactly when the Core is logging
                // "Percy is not running". The file copy above is what survives.
            }
        }
    }
}
