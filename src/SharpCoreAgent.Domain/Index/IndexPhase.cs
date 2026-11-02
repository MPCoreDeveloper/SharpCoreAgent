namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// One step of a build, with the wall clock it took and the processor time it cost.
/// </summary>
/// <remarks>
/// A build is a handful of steps whose costs differ by two orders of magnitude, and one total cannot say which of
/// them a developer is waiting for (spec 002, `research.md` D18). Both clocks are here for the same reason they are
/// on the whole report: a step that spent a hundred seconds of wall clock and two seconds of processor time was
/// waiting, not working.
/// <para>
/// A build reports every one of its steps, in the order it runs them, including the ones that did nothing — so a
/// reader can add them up and see what is unaccounted for instead of guessing which numbers are missing.
/// <see cref="Nested"/> marks the one step that is not a sibling: the engine's commit runs inside whichever step
/// wrote last, so its time is already counted in that step's.
/// </para>
/// <para>
/// The processor time is process-wide and comes from an operating-system counter that is updated a few times a
/// second at best, so a step that took microseconds reports zero there. Read a zero as "below the resolution",
/// never as "no work".
/// </para>
/// </remarks>
public sealed record IndexPhase
{
    /// <summary>
    /// Gets the name of the step, as the command line prints it: <c>scan</c>, <c>load</c>, <c>write</c>,
    /// <c>remove</c>, <c>edges</c>, <c>communities</c>, <c>meta</c> or <c>commit</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the elapsed time the step took.
    /// </summary>
    public TimeSpan Wall { get; init; }

    /// <summary>
    /// Gets the processor time the step cost, process-wide.
    /// </summary>
    public TimeSpan Processor { get; init; }

    /// <summary>
    /// Gets a value indicating whether this step's time is already counted inside another step's.
    /// </summary>
    /// <remarks>
    /// Only the engine's commit is nested, and it is nested four times over: the same commit runs inside the writing
    /// pass, the pass that drops vanished files, the edge resolution and the <c>meta</c> write. It gets one line
    /// rather than four because the question it answers — how much of the run the store charges for touching it —
    /// is one question.
    /// </remarks>
    public bool Nested { get; init; }
}
