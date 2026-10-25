namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// One citable block: the unit of every retrieval answer.
/// </summary>
/// <remarks>
/// A block is always a contiguous, line-addressed slice of one file, so a caller can quote it back with
/// <c>path:start-end</c> and a reader can open exactly those lines. <see cref="Sha8"/> is the passage's
/// own hash, not the file's: it stays the same while the passage does, which is what lets a later turn
/// tell "same evidence" from "same lines, different code".
/// <para>
/// <see cref="Legs"/> is a set, not a winner. Two retrievers agreeing on a block is the strongest signal
/// the index can produce, and the tool output says so (<c>why: lexical,vector</c>) because that is the
/// detail that tells a reader whether to trust the citation.
/// </para>
/// </remarks>
public sealed record CodeEvidence
{
    /// <summary>
    /// Gets the workspace-relative path, with <c>/</c> separators.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the first line of the passage, 1-based and inclusive.
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// Gets the last line of the passage, 1-based and inclusive.
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// Gets the first eight hexadecimal characters of the passage's SHA-256.
    /// </summary>
    public required string Sha8 { get; init; }

    /// <summary>
    /// Gets the passage text exactly as it was indexed.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets what the passage was selected as: <c>chunk</c>, a declaration kind, <c>doc</c> or <c>config</c>.
    /// </summary>
    public string Kind { get; init; } = "chunk";

    /// <summary>
    /// Gets the qualified name of the declaration the passage holds, when it holds one.
    /// </summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// Gets the relevance score, after fusion and boosting; higher is better.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Gets the retrieval legs that produced this block, in <see cref="EvidenceLeg"/> order.
    /// </summary>
    public IReadOnlyList<EvidenceLeg> Legs { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the file changed after this passage was indexed.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>
    /// Gets how the block was reached — <c>calls</c>, <c>called_by</c>, <c>inherits</c>, <c>implements</c>,
    /// <c>imports</c>, <c>declares</c>, <c>doc_mentions</c> — or <see langword="null"/> when it was matched
    /// by text alone.
    /// </summary>
    public string? Relation { get; init; }
}
