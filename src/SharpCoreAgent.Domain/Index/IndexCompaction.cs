namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// What one compaction did: the file before it, the file after it, and how long giving the difference back took.
/// </summary>
/// <remarks>
/// The four numbers are the whole report because the work has no counters of its own: a compaction rewrites the pages
/// the index already holds, so it changes the size of the file and nothing else. <see cref="ReclaimedBytes"/> is derived
/// rather than stored, so the three sizes cannot disagree with each other.
/// <para>
/// <see cref="Succeeded"/> is false when the compaction could not run at all — another process holds the writer lock, or
/// the store refused the rewrite — and <see cref="Note"/> carries the sentence that says which. A compaction that had
/// nothing to reclaim did succeed: the file is the size its rows need.
/// </para>
/// </remarks>
public sealed record IndexCompaction
{
    /// <summary>
    /// Gets the state of the index after the compaction.
    /// </summary>
    /// <remarks>
    /// Read after the file was rewritten, so <see cref="IndexStatus.SizeBytes"/> is the compacted size: a caller that
    /// prints this status prints the file that is now on disk rather than the one that was there before.
    /// </remarks>
    public required IndexStatus Status { get; init; }

    /// <summary>
    /// Gets the size of the index file before the compaction, in bytes.
    /// </summary>
    public long BeforeBytes { get; init; }

    /// <summary>
    /// Gets the size of the index file after the compaction, in bytes.
    /// </summary>
    public long AfterBytes { get; init; }

    /// <summary>
    /// Gets how many bytes the compaction gave back.
    /// </summary>
    /// <remarks>
    /// Zero when there was nothing to reclaim, and zero when the compaction did not run or did not succeed — which is
    /// why <see cref="Succeeded"/> and not this number is the answer to "did it work".
    /// </remarks>
    public long ReclaimedBytes => Math.Max(0, BeforeBytes - AfterBytes);

    /// <summary>
    /// Gets how long the compaction took, writer lock included, in milliseconds.
    /// </summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>
    /// Gets a value indicating whether the compaction ran.
    /// </summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>
    /// Gets the one line worth printing beside the numbers, or <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// For example <c>there is no index to compact</c> or the store's own message when it refused the rewrite.
    /// </remarks>
    public string? Note { get; init; }
}
