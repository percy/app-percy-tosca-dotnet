namespace AppPercyTosca.Core
{
    /// A running `percy` process, reduced to what the lifecycle logic needs.
    ///
    /// Abstracted for the same reason the device session is: starting a process is the one thing CI
    /// cannot do here, and the decisions worth testing — when to give up, what counts as ready, which
    /// line carries the build link — should not require one.
    public interface ICliProcess : IDisposable
    {
        /// Whether the process has already finished. A start that exits is a failure by definition:
        /// `app:exec:start` is meant to keep running until it is stopped.
        bool HasExited { get; }

        /// Exit code once <see cref="HasExited"/>, otherwise meaningless.
        int ExitCode { get; }

        /// The next line of merged stdout/stderr, or null when the stream ends or nothing arrives
        /// within <paramref name="timeout"/>. Both are the same answer to the caller: no more to read.
        string? ReadLine(TimeSpan timeout);

        /// Ends the process. Called only when startup failed — a successful start must outlive the
        /// step, since the whole point is for later steps to snapshot against it.
        void Kill();
    }

    /// Starts `percy` with the given arguments.
    public interface ICliLauncher
    {
        /// <param name="arguments">Everything after the executable, e.g. <c>app:exec:start</c>.</param>
        ICliProcess Start(string arguments);
    }
}
