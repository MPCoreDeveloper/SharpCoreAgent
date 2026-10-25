namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// What the index is, how healthy it is and what it currently holds.
/// </summary>
/// <remarks>
/// This is the record every "is it there?" question ends in. It is a value, not a handle: a status can be
/// cached, logged, compared and printed without keeping a database open, and a missing index is a status
/// with <see cref="IndexFreshness.Missing"/> rather than an exception.
/// </remarks>
public sealed record IndexStatus
{
    /// <summary>
    /// Gets the absolute workspace root the index belongs to.
    /// </summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>
    /// Gets the current health.
    /// </summary>
    public required IndexFreshness Freshness { get; init; }

    /// <summary>
    /// Gets the detail tier the index was built with.
    /// </summary>
    public required IndexTier Tier { get; init; }

    /// <summary>
    /// Gets the embedder identity, for example <c>hashed-local/512</c>, or <c>off</c>.
    /// </summary>
    public required string Embedder { get; init; }

    /// <summary>
    /// Gets the vector width, or 0 when no vectors are stored.
    /// </summary>
    public required int Dimensions { get; init; }

    /// <summary>
    /// Gets how many files the last build indexed.
    /// </summary>
    public int Files { get; init; }

    /// <summary>
    /// Gets how many passages the index holds.
    /// </summary>
    public int Chunks { get; init; }

    /// <summary>
    /// Gets how many declarations the index holds.
    /// </summary>
    public int Symbols { get; init; }

    /// <summary>
    /// Gets how many typed relations the index holds.
    /// </summary>
    public int Edges { get; init; }

    /// <summary>
    /// Gets how many indexed files changed or failed since the last build.
    /// </summary>
    public int StaleFiles { get; init; }

    /// <summary>
    /// Gets how many files were deliberately skipped during the last build.
    /// </summary>
    public int SkippedFiles { get; init; }

    /// <summary>
    /// Gets how many stored rows point at a file that no longer has a row of its own.
    /// </summary>
    /// <remarks>
    /// An orphan is the fingerprint of a build that was interrupted between deleting a file's rows and
    /// inserting the replacements, so a non-zero count is a health fact and not an error: the status reports
    /// it, the next refresh repairs it.
    /// </remarks>
    public int OrphanRows { get; init; }

    /// <summary>
    /// Gets a value indicating whether the vector leg can answer a query.
    /// </summary>
    public bool VectorSearchEnabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether retrieval falls back to lexical-only, because vectors are absent or
    /// the embedder that produced them is not the configured one.
    /// </summary>
    public bool LexicalFallback { get; init; }

    /// <summary>
    /// Gets when the last complete build finished, or <see langword="null"/> when there has never been one.
    /// </summary>
    public DateTimeOffset? BuiltUtc { get; init; }

    /// <summary>
    /// Gets the on-disk size of the index files, in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Creates the status of a workspace that has never been indexed.
    /// </summary>
    /// <param name="workspaceRoot">The absolute workspace root.</param>
    /// <returns>
    /// A status with <see cref="IndexFreshness.Missing"/>, the lean tier and no embedder; there is nothing
    /// stored to describe, so <see cref="Embedder"/> is <c>off</c> and every count is zero.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot"/> is blank.</exception>
    public static IndexStatus Missing(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        return new IndexStatus
        {
            WorkspaceRoot = workspaceRoot,
            Freshness = IndexFreshness.Missing,
            Tier = IndexTier.Lean,
            Embedder = "off",
            Dimensions = 0,
        };
    }
}
