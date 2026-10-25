namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// One passage: the unit the index stores, ranks and cites.
/// </summary>
/// <remarks>
/// Lines are 1-based and inclusive, matching what <c>read_file</c> takes, so a citation can be handed straight to
/// the tool that opens it without arithmetic in between. <see cref="SymbolKey"/> is set when the passage starts at a
/// declaration, which is what lets a search for an exact identifier prefer the passage that declares it.
/// </remarks>
internal sealed record Passage
{
    /// <summary>Gets the 1-based first line, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line, inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the passage kind: <c>type</c>, <c>member</c> or <c>block</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the passage text, exactly as the file holds it.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the key of the symbol this passage starts at, or <see langword="null"/>.</summary>
    public string? SymbolKey { get; init; }

    /// <summary>Gets the qualified name of the symbol this passage starts at, or <see langword="null"/>.</summary>
    public string? SymbolName { get; init; }

    /// <summary>Gets how many lines the passage spans.</summary>
    public int LineCount => EndLine - StartLine + 1;
}

/// <summary>
/// The passage kinds stored in <c>chunks.kind</c>.
/// </summary>
internal static class PassageKinds
{
    /// <summary>A passage that starts at a type declaration.</summary>
    public const string Type = "type";

    /// <summary>A passage that starts at a member declaration.</summary>
    public const string Member = "member";

    /// <summary>A passage that starts anywhere else: a comment block, a config file, a continuation.</summary>
    public const string Block = "block";
}
