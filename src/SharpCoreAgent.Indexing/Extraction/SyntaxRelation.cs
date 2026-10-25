namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// One typed relation the extractor found, named by symbol keys rather than row ids.
/// </summary>
/// <remarks>
/// Names, not ids, because at extraction time the target may not have been indexed yet — it can live in a file
/// the build has not reached, or in another package entirely. The writer resolves names to ids once every file is
/// in, and drops what it cannot resolve.
/// </remarks>
internal sealed record SyntaxRelation
{
    /// <summary>Gets the key of the symbol the relation belongs to, or <see langword="null"/> for a file-level
    /// relation such as an import.</summary>
    public string? Source { get; init; }

    /// <summary>Gets the name the relation points at, as written: a callee, a base type, a namespace.</summary>
    public required string Target { get; init; }

    /// <summary>Gets the relation kind, one of <see cref="RelationKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets how strong the relation is; call sites are compressed with a cap, everything else is 1.</summary>
    public int Weight { get; init; } = 1;
}

/// <summary>
/// One documentation passage: a section of a Markdown file, or a doc comment that stands on its own.
/// </summary>
/// <remarks>
/// The index stores documentation because half the answer to "why is this like this" is written next to the code
/// rather than in it. A heading is kept because it is usually the symbol's name, which is what lets the writer
/// attach the passage to a symbol and the ranker boost it when the query names that symbol.
/// </remarks>
internal sealed record ExtractedDoc
{
    /// <summary>Gets the 1-based first line of the passage, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line of the passage, inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the heading the passage sits under, without <c>#</c> markers, or <see langword="null"/>.</summary>
    public string? Heading { get; init; }

    /// <summary>Gets the passage text, which is what gets tokenised and searched.</summary>
    public required string Text { get; init; }
}
