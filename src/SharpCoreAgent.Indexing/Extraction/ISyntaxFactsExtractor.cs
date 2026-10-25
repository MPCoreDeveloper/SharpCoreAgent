namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// Everything one file contributes: its declarations, the relations between them, the documentation it holds and
/// the namespaces it imports.
/// </summary>
internal sealed record SyntaxFacts
{
    /// <summary>Gets the extractor that produced these facts, for example <c>heuristic/1</c>.</summary>
    public required string Extractor { get; init; }

    /// <summary>Gets the declarations, in the order they appear in the file.</summary>
    public required IReadOnlyList<ExtractedSymbol> Symbols { get; init; }

    /// <summary>Gets the relations, one entry per (source, target, kind), already compressed by weight.</summary>
    public required IReadOnlyList<SyntaxRelation> Relations { get; init; }

    /// <summary>Gets the documentation passages, in file order.</summary>
    public required IReadOnlyList<ExtractedDoc> Docs { get; init; }

    /// <summary>Gets the namespaces, modules or packages the file imports, in file order, without duplicates.</summary>
    public required IReadOnlyList<string> Imports { get; init; }

    /// <summary>Gets an empty result, which is what a file the extractor cannot read produces.</summary>
    /// <param name="extractor">The extractor's id.</param>
    /// <returns>A result with nothing in it.</returns>
    public static SyntaxFacts Empty(string extractor) => new()
    {
        Extractor = extractor,
        Symbols = [],
        Relations = [],
        Docs = [],
        Imports = [],
    };
}

/// <summary>
/// The seam the real parser will land on.
/// </summary>
/// <remarks>
/// Nothing downstream knows how facts are produced: the writer takes a <see cref="SyntaxFacts"/> and the retrieval
/// legs read rows. That is deliberate — the heuristic implementation here is a braces-and-regex reading of the
/// file, and the day a Roslyn-backed implementation replaces it, the schema, the writer and every tool stay as
/// they are. The <see cref="Id"/> is stored in <c>meta['parser_version']</c> so a change of reading invalidates a
/// build instead of quietly mixing two vocabularies.
/// </remarks>
internal interface ISyntaxFactsExtractor
{
    /// <summary>Gets the identity of the reading, dimensions of behaviour included (for example
    /// <c>heuristic/1</c>).</summary>
    string Id { get; }

    /// <summary>
    /// Extracts one file's facts.
    /// </summary>
    /// <param name="file">The scanned file, whose path decides the language when the text does not say.</param>
    /// <param name="text">The file's text, already decoded.</param>
    /// <param name="cancellationToken">Cancels between declarations.</param>
    /// <returns>The facts; empty rather than exceptional when the text is unreadable.</returns>
    SyntaxFacts Extract(ScannedFile file, string text, CancellationToken cancellationToken);
}
