using AppPercyTosca.Core;

namespace AppPercyTosca.Core.Tests
{
    /// A scripted `percy` process: lines are handed out in order, then null forever.
    public class StubCliProcess : ICliProcess
    {
        private readonly Queue<string> _lines;

        public StubCliProcess(params string[] lines) => _lines = new Queue<string>(lines);

        public bool HasExited { get; set; }
        public int ExitCode { get; set; }
        public int KillCount { get; private set; }
        public bool Disposed { get; private set; }
        public List<TimeSpan> ReadTimeouts { get; } = new List<TimeSpan>();

        public string? ReadLine(TimeSpan timeout)
        {
            ReadTimeouts.Add(timeout);
            return _lines.Count > 0 ? _lines.Dequeue() : null;
        }

        public void Kill() => KillCount++;

        public void Dispose() => Disposed = true;
    }

    /// Hands out a prepared process and records what it was asked to run.
    public class StubCliLauncher : ICliLauncher
    {
        private readonly Func<string, ICliProcess> _start;

        public StubCliLauncher(ICliProcess process) => _start = _ => process;

        public StubCliLauncher(Func<string, ICliProcess> start) => _start = start;

        public List<string> Arguments { get; } = new List<string>();

        public ICliProcess Start(string arguments)
        {
            Arguments.Add(arguments);
            return _start(arguments);
        }
    }
}
