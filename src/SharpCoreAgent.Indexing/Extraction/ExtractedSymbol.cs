namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// One declaration the extractor found, with the range a reader can open and the key edges are named by.
/// </summary>
/// <remarks>
/// The key is the identity: <c>&lt;namespace&gt;.&lt;type&gt;[.&lt;member&gt;]#&lt;kind&gt;</c>, with no whitespace. It survives a
/// rebuild while row ids do not, so an edge written in one build still means the same symbol in the next. The
/// qualified name is the same path without the <c>#kind</c> suffix, because that is what a human — or a model —
/// types when it asks about a symbol.
/// </remarks>
internal sealed record ExtractedSymbol
{
    /// <summary>Gets the stable key, for example <c>Demo.Geometry.Circle.Area#method</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the name alone, for example <c>Area</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the dotted name without the kind suffix, for example <c>Demo.Geometry.Circle.Area</c>.</summary>
    public required string QualifiedName { get; init; }

    /// <summary>Gets the kind: <c>class</c>, <c>interface</c>, <c>record</c>, <c>struct</c>, <c>enum</c>,
    /// <c>method</c>, <c>property</c> or <c>field</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the 1-based first line of the declaration, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line of the declaration, inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the namespace the declaration sits in, or <see langword="null"/> for a file without one.</summary>
    public string? Namespace { get; init; }

    /// <summary>Gets the enclosing type's qualified name, or <see langword="null"/> at namespace level.</summary>
    public string? Container { get; init; }

    /// <summary>Gets the declaration line with its modifiers, trimmed to one line, or <see langword="null"/> for
    /// a type whose header is longer than the line it starts on.</summary>
    public string? Signature { get; init; }

    /// <summary>Gets the documentation the declaration carries, tags stripped, or <see langword="null"/>.</summary>
    public string? Doc { get; init; }

    /// <summary>Gets a value indicating whether this symbol owns other symbols.</summary>
    public bool IsContainer => Kind is SymbolKinds.Class or SymbolKinds.Interface or SymbolKinds.Record
        or SymbolKinds.Struct or SymbolKinds.Enum;
}
