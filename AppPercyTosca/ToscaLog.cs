using System.Reflection;
using System.Runtime.CompilerServices;
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
        /// Where lines are written. Defaults to percy.txt in the temp directory — the same filename
        /// the HTML SDK uses, so anyone who has debugged that one knows where to look.
        ///
        /// Overridable via <c>PERCY_LOG_FILE</c> because the default depends on which account Tosca
        /// runs as: %TEMP% for a service or a different user is not the %TEMP% you see in your own
        /// shell, and hunting for the file is a poor first debugging step.
        /// </summary>
        internal static readonly string LogPath =
            Environment.GetEnvironmentVariable("PERCY_LOG_FILE") is string custom
                && !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : Path.Combine(Path.GetTempPath(), "percy.txt");

        private static readonly object FileLock = new object();

        /// <summary>
        /// Runs when the CLR loads this assembly, before anything else in it executes.
        ///
        /// This exists to answer one question that is otherwise very hard to answer: did Tosca load
        /// this DLL at all? Every other log line is written from the task, so if Tosca fails to
        /// register the task — "The SpecialExecutionTask ... was not found for engine ..." — nothing
        /// gets written and an empty log looks identical to a missing file. A line here separates
        /// "never loaded" from "loaded but not registered", which are different problems with
        /// different fixes.
        /// </summary>
        // CA2255 warns off module initializers in libraries, because a library that runs code on load
        // surprises its consumers. Suppressed deliberately: this assembly is not a library anyone
        // references — it is a plugin Tosca discovers by scanning a folder, and recording its own load
        // is the one thing no later hook can do. It writes a single line and cannot throw.
#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void RecordAssemblyLoad()
        {
            try
            {
                Assembly self = typeof(ToscaLog).Assembly;
                WriteToFile($"[percy:tosca] assembly loaded: {self.GetName().Name} " +
                    $"{self.GetName().Version} from {self.Location}");
            }
            catch (Exception)
            {
                // Nothing may throw out of a module initializer: it would fault the assembly load
                // itself and turn a logging problem into a broken extension.
            }
        }
#pragma warning restore CA2255

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
                // Timestamped so a line can be tied to a particular run: this file is appended to
                // across every run until someone deletes it.
                string stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";

                // Locked because Tosca can run test steps concurrently, and two appends racing on
                // the same handle lose lines.
                lock (FileLock)
                {
                    File.AppendAllText(LogPath, stamped + Environment.NewLine);
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
                AppPercyScreenshot.LogClient.PostLog(message, level);
            }
            catch (Exception)
            {
                // Expected whenever Percy is not running, which is exactly when the Core is logging
                // "Percy is not running". The file copy above is what survives.
            }
        }
    }
}
