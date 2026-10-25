using SharpCoreDB.Search.Lexical;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// One passage as retrieval reads it: where it is, what it holds and which declaration it opens.
/// </summary>
/// <remarks>
/// The row is a copy of the stored one, not a live view. A query therefore answers from a consistent snapshot
/// even while another process is replacing a file's rows, which is what lets a citation the model already saw
/// stay meaningful for the rest of the turn.
/// </remarks>
internal sealed record ChunkRow
{
    /// <summary>Gets the <c>chunks.id</c>.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the owning <c>files.id</c>.</summary>
    public required long FileId { get; init; }

    /// <summary>Gets the workspace-relative path, with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the 1-based first line, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line, inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the passage kind: <c>type</c>, <c>member</c> or <c>block</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the key of the declaration the passage starts at, or <see langword="null"/>.</summary>
    public string? SymbolKey { get; init; }

    /// <summary>Gets the qualified name of the declaration the passage starts at, or <see langword="null"/>.</summary>
    public string? SymbolName { get; init; }

    /// <summary>Gets the passage text, exactly as it was indexed.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the first eight characters of the passage's SHA-256, which is what a citation shows.</summary>
    public required string Sha8 { get; init; }

    /// <summary>
    /// Gets the stored embedding, or <see langword="null"/> when the index was built with vectors off.
    /// </summary>
    /// <remarks>
    /// Held with the passage because the snapshot already carries its text and tokens: the vector is the third of the
    /// three things every leg reads, and loading it here is what lets the vector leg answer without a second query.
    /// </remarks>
    public float[]? Vector { get; init; }
}

/// <summary>
/// One declaration as retrieval reads it.
/// </summary>
internal sealed record SymbolRow
{
    /// <summary>Gets the <c>symbols.id</c>, which is what edges name.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the owning <c>files.id</c>.</summary>
    public required long FileId { get; init; }

    /// <summary>Gets the stable key, for example <c>Demo.Geometry.Circle.Area#method</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the short name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the dotted qualified name.</summary>
    public required string QualifiedName { get; init; }

    /// <summary>Gets the declaration kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the 1-based first line, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line, inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the workspace-relative path of the declaring file.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the declaration line as written, when the language gives one.</summary>
    public string? Signature { get; init; }

    /// <summary>Gets the community label the last build assigned, for example <c>c4</c>.</summary>
    public string? Community { get; init; }

    /// <summary>Gets the degree centrality the last build computed.</summary>
    public double Centrality { get; init; }
}

/// <summary>
/// One stored relation, as the graph legs read it.
/// </summary>
/// <param name="Source">The symbol the relation starts at.</param>
/// <param name="Target">The symbol it points at.</param>
/// <param name="Relation">One of <see cref="Extraction.RelationKinds"/>.</param>
/// <param name="Weight">How strongly it holds.</param>
internal readonly record struct EdgeRow(long Source, long Target, string Relation, double Weight);

/// <summary>
/// One indexed file as retrieval reads it: where it is, what language it is, whether it is trustworthy and how
/// recently it was written.
/// </summary>
/// <remarks>
/// The ranking needs all four. <see cref="Language"/> turns the <c>doc</c> and <c>config</c> query kinds into a
/// filter that can actually match something, <see cref="State"/> is what makes a block report itself as stale, and
/// <see cref="ModifiedUtc"/> is the only input to the recency boost — passed in as a value, never read from a clock
/// inside ranking, because a score that depends on when it was computed cannot be replayed.
/// </remarks>
internal sealed record FileRow
{
    /// <summary>Gets the <c>files.id</c>.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the workspace-relative path, with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the language the scanner assigned, for example <c>csharp</c> or <c>markdown</c>.</summary>
    public required string Language { get; init; }

    /// <summary>Gets the stored state: <c>ok</c>, <c>skipped</c>, <c>stale</c> or <c>error</c>.</summary>
    public required string State { get; init; }

    /// <summary>Gets the file's last-write timestamp as the build recorded it, or <see langword="null"/>.</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>Gets a value indicating whether the build read this file and its rows are current.</summary>
    public bool IsIndexed => string.Equals(State, IndexFileStates.Ok, StringComparison.Ordinal);

    /// <summary>Gets a value indicating whether the file changed after its rows were written.</summary>
    public bool IsStale => string.Equals(State, IndexFileStates.Stale, StringComparison.Ordinal);
}

/// <summary>
/// One immutable view of a build: its passages, its declarations, its edges and the lexical index over them.
/// </summary>
/// <remarks>
/// Loaded once per <c>build_id</c> and shared by every query until the build changes. The alternative — reading
/// rows per query — would put a database round-trip in the middle of ranking, where the whole point is that a
/// model's question is answered from memory.
/// </remarks>
internal sealed class IndexSnapshot
{
    /// <summary>Gets the build the snapshot describes; an empty string for a schema that was never built.</summary>
    public required string BuildId { get; init; }

    /// <summary>Gets every passage, by id.</summary>
    public required IReadOnlyDictionary<long, ChunkRow> Chunks { get; init; }

    /// <summary>Gets every declaration, by id.</summary>
    public required IReadOnlyDictionary<long, SymbolRow> Symbols { get; init; }

    /// <summary>Gets every relation, in insertion order.</summary>
    public required IReadOnlyList<EdgeRow> Edges { get; init; }

    /// <summary>Gets the BM25 index over <c>chunks.tokens</c>.</summary>
    public required FullTextIndex Lexical { get; init; }

    /// <summary>Gets the workspace-relative path of every indexed file, by <c>files.id</c>.</summary>
    public required IReadOnlyDictionary<long, string> Paths { get; init; }

    /// <summary>Gets every indexed file, by <c>files.id</c>.</summary>
    public required IReadOnlyDictionary<long, FileRow> Files { get; init; }

    /// <summary>
    /// Gets the symbol keys a documentation passage in <c>docs</c> points at.
    /// </summary>
    /// <remarks>
    /// The doc boost asks one question — "does the workspace's prose talk about this declaration?" — and a set of
    /// keys answers it without loading the prose itself, which stays available to the reader through the block.
    /// </remarks>
    public required IReadOnlySet<string> DocumentedSymbols { get; init; }

    /// <summary>Gets the embedder identity stored in <c>meta['embedder']</c>, or <c>off</c>.</summary>
    public required string Embedder { get; init; }

    /// <summary>Gets the stored vector width, or zero when no vectors are stored.</summary>
    public required int Dimensions { get; init; }

    /// <summary>Gets a value indicating whether the vector leg can answer a query.</summary>
    public bool HasVectors => Dimensions > 0 && !string.Equals(Embedder, "off", StringComparison.Ordinal);

    /// <summary>Gets how many passages the snapshot holds.</summary>
    public int ChunkCount => Chunks.Count;

    /// <summary>Gets how many declarations the snapshot holds.</summary>
    public int SymbolCount => Symbols.Count;
}
