namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// Which clock a build's budget is measured on.
/// </summary>
/// <remarks>
/// The choice belongs to the caller because two callers want opposite things from the same number.
/// <para>
/// A turn-start refresh holds up a model: its three seconds are wall clock, because what the developer waits
/// for is a request, not a processor.
/// </para>
/// <para>
/// A command typed by a developer asks for work, and work is processor time. A machine that spent eight hours in
/// Modern Standby during such a run did not spend the hour the command allows — the process was not scheduled, so
/// it was neither working nor overrunning. Measuring wall clock there turned a ten-minute index into a
/// <c>partial</c> one and a report that said thirty thousand seconds: spec 002, D19.
/// </para>
/// </remarks>
public enum IndexBudgetClock
{
    /// <summary>
    /// Wall clock, the wait for the writer lock included: what a caller who is waiting measures. The default, and
    /// what every caller got before the distinction existed.
    /// </summary>
    WallClock,

    /// <summary>
    /// Processor time of this process: what a caller who asked for work measures. Time the machine was suspended
    /// or otherwise not running this process does not count against it, and waiting for a lock is not charged to it
    /// either — a build gets its allowance of work regardless of how long the queuing took.
    /// </summary>
    ProcessorTime,
}
