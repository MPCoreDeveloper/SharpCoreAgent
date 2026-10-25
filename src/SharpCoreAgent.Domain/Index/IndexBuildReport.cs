namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// What one build, refresh or prune did, in the numbers a caller reports.
/// </summary>
/// <remarks>
/// The counters are disjoint: every scanned file lands in exactly one of added, changed, removed,
/// unchanged or skipped, which is what makes "0 changed" a meaningful assertion in a test and a useful
/// line in a terminal. <see cref="Incomplete"/> is separate from failure: a budget that ran out leaves a
/// usable index behind and the next run picks up where this one stopped.
/// </remarks>
public sealed record IndexBuildReport
{
    /// <summary>
    /// Gets the state of the index after the work finished.
    /// </summary>
    public required IndexStatus Status { get; init; }

    /// <summary>
    /// Gets how many files the build indexed for the first time.
    /// </summary>
    public int Added { get; init; }

    /// <summary>
    /// Gets how many indexed files the build rewrote because their content changed.
    /// </summary>
    public int Changed { get; init; }

    /// <summary>
    /// Gets how many indexed files the build dropped because they no longer exist.
    /// </summary>
    public int Removed { get; init; }

    /// <summary>
    /// Gets how many files the build looked at and left alone.
    /// </summary>
    public int Unchanged { get; init; }

    /// <summary>
    /// Gets how many files were skipped by policy, for example because they are binary or too large.
    /// </summary>
    public int Skipped { get; init; }

    /// <summary>
    /// Gets how long the work took, in milliseconds.
    /// </summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>
    /// Gets a value indicating whether the work stopped before the workspace was exhausted.
    /// </summary>
    public bool Incomplete { get; init; }

    /// <summary>
    /// Gets one human-readable line per decision worth explaining, for example
    /// <c>docs/README.md: larger than 1024 KiB</c>.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
