using System.Collections.Concurrent;
using System.Diagnostics;
using AppPercyTosca.Core;

namespace AppPercyTosca
{
    /// Runs the Percy CLI as a child process.
    ///
    /// In the shim rather than the Core because it is the one part CI cannot exercise: no runner has a
    /// Percy CLI, and the decisions worth testing live in <see cref="PercyCliLifecycle"/> behind
    /// <see cref="ICliLauncher"/>.
    internal class CliLauncher : ICliLauncher
    {
        private readonly string _command;
        private readonly string? _token;

        /// <param name="command">
        /// The CLI entry point, default `percy`. Overridable because npm may have installed it
        /// somewhere Tosca's PATH does not reach, and a full path is then the only way in.
        /// </param>
        /// <param name="token">
        /// Passed to the child through its environment, never on the command line: an argument list is
        /// readable by any process on the machine, and it would also land in the CLI's own echo of how
        /// it was invoked.
        /// </param>
        internal CliLauncher(string? command, string? token)
        {
            _command = string.IsNullOrWhiteSpace(command) ? "percy" : command.Trim();
            _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        public ICliProcess Start(string arguments)
        {
            // Through cmd.exe because npm installs `percy` as percy.cmd, which CreateProcess cannot
            // execute directly — Process.Start would report "the specified executable is not a valid
            // application" and read as a missing install.
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{_command} {arguments}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (_token != null) start.Environment["PERCY_TOKEN"] = _token;

            // The CLI reads these too, and on Tosca they can only have come from a module parameter.
            foreach (string name in new[] { "PERCY_CLI_API", "PERCY_LOGLEVEL" })
            {
                string? supplied = Env.Read(name);
                if (!string.IsNullOrWhiteSpace(supplied)) start.Environment[name] = supplied;
            }

            return new CliProcess(Process.Start(start)
                ?? throw new PercyException($"Could not start '{_command} {arguments}'."));
        }
    }

    /// One child process, with both streams merged into a queue.
    ///
    /// The streams must keep being drained even after startup: a redirected pipe holds only a few
    /// kilobytes, and a CLI whose stdout nobody reads blocks on its next write — which would freeze
    /// Percy partway through a run, long after the step that started it passed.
    internal class CliProcess : ICliProcess
    {
        private readonly Process _process;
        private readonly BlockingCollection<string> _lines = new BlockingCollection<string>();

        internal CliProcess(Process process)
        {
            _process = process;

            _process.OutputDataReceived += Collect;
            _process.ErrorDataReceived += Collect;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private void Collect(object sender, DataReceivedEventArgs e)
        {
            // A null Data is the stream closing; adding nothing keeps ReadLine's contract that null
            // means "no more", rather than queueing a null nobody can distinguish from a blank line.
            if (e.Data == null || _lines.IsAddingCompleted) return;
            try
            {
                _lines.Add(e.Data);
            }
            catch (InvalidOperationException)
            {
                // Completed between the check and the add; the process is going away regardless.
            }
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public string? ReadLine(TimeSpan timeout) =>
            _lines.TryTake(out string? line, (int)timeout.TotalMilliseconds) ? line : null;

        public void Kill()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception e)
            {
                // Already gone, or not ours to kill. Either way there is nothing else to try.
                Utils.Log($"Could not stop the Percy CLI process: {e.Message}", "debug");
            }
        }

        /// Disposes the handle only. A successfully started CLI must outlive this object — the whole
        /// point is for later steps to snapshot against it — so nothing here kills the process.
        public void Dispose()
        {
            _lines.CompleteAdding();
            _process.Dispose();
        }
    }
}
