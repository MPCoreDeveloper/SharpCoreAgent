namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// The result of comparing the index against the workspace on disk.
/// </summary>
/// <remarks>
/// The four counters name the four ways an index and a directory tree can disagree, and they are counted
/// separately because each one has a different repair: a missing file needs an insert, a vanished file a
/// delete, a hash mismatch a rewrite, and an orphan a delete that the last build never finished.
/// </remarks>
public sealed record IndexVerification
{
    /// <summary>
    /// Gets a value indicating whether all four counters are zero.
    /// </summary>
    public required bool IsHealthy { get; init; }

    /// <summary>
    /// Gets how many files on disk are not in the index.
    /// </summary>
    public int FilesMissingFromIndex { get; init; }

    /// <summary>
    /// Gets how many indexed files no longer exist on disk.
    /// </summary>
    public int FilesMissingOnDisk { get; init; }

    /// <summary>
    /// Gets how many indexed files have a different content hash than the file on disk.
    /// </summary>
    public int HashMismatches { get; init; }

    /// <summary>
    /// Gets how many rows point at a file that no longer has a row of its own.
    /// </summary>
    public int Orphans { get; init; }

    /// <summary>
    /// Gets one line per disagreement, ready to print under the summary.
    /// </summary>
    public IReadOnlyList<string> Details { get; init; } = [];
}
