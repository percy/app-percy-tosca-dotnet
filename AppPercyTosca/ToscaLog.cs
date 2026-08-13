using System.Reflection;
using System.Runtime.CompilerServices;
using AppPercyTosca.Core;

namespace AppPercyTosca
{
    /// Where the SDK's log lines go. Commander has no console, so lines are forwarded to the Percy CLI
    /// to be interleaved with the build's output, and mirrored to a file so that a failure to reach
    /// the CLI — the very failure the line is most likely reporting — still leaves a record.
    internal static class ToscaLog
    {
        /// percy.txt in the temp directory, matching the HTML SDK. Overridable via the module's
        /// <c>LogFile</c> parameter or <c>PERCY_LOG_FILE</c>, because %TEMP% resolves per-account and
        /// Tosca may not run as you.
        ///
        /// A property, not a readonly field: a field is fixed at type load, which is before any step
        /// has supplied its parameters.
        internal static string LogPath => Env.LogFile();

        private static readonly object FileLock = new object();

        /// Answers the one question that is otherwise very hard to answer: did Tosca load this DLL at
        /// all? Every other line is written from the task, so if the task is never registered nothing
        /// is written and an empty log looks identical to a missing one. This separates "never loaded"
        /// from "loaded but not registered", which have different fixes.
        // CA2255 warns off module initializers in libraries. This is not a library anyone references —
        // it is a plugin Tosca discovers by scanning a folder — and recording its own load is the one
        // thing no later hook can do.
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
                // A throw here faults the assembly load and turns a logging problem into a dead plugin.
            }
        }
#pragma warning restore CA2255

        /// Installed as <see cref="Utils.LogSink"/>; never throws, since the Core falls back to stdout.
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
                // Timestamped: this file is appended to across every run until someone deletes it.
                string stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";

                // Tosca can run steps concurrently, and two appends racing on one handle lose lines.
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
                // Expected whenever Percy is down, which is when the Core logs that it is. The file
                // copy above is what survives.
            }
        }
    }
}
