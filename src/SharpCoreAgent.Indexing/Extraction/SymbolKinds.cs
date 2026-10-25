namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// The declaration kinds the index stores, drawn from the closed list <c>CodeQuery.Kind</c> accepts.
/// </summary>
/// <remarks>
/// A top-level function in a language without types (Python, Go, Rust) is stored as <c>method</c>: the kind list
/// is what a model filters by, and "method" is the only callable in it. Adding a <c>function</c> kind would mean
/// changing the query contract, so the vocabulary stays closed and the mapping is documented here instead.
/// </remarks>
internal static class SymbolKinds
{
    /// <summary>A type with members.</summary>
    public const string Class = "class";

    /// <summary>A contract.</summary>
    public const string Interface = "interface";

    /// <summary>A record.</summary>
    public const string Record = "record";

    /// <summary>A value type.</summary>
    public const string Struct = "struct";

    /// <summary>A closed set of values.</summary>
    public const string Enum = "enum";

    /// <summary>A callable, including a top-level function.</summary>
    public const string Method = "method";

    /// <summary>A property or accessor pair.</summary>
    public const string Property = "property";

    /// <summary>A field, constant or module-level variable.</summary>
    public const string Field = "field";

    /// <summary>Gets every kind a stored symbol may have, in the query contract's order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Class, Interface, Record, Struct, Enum, Method, Property, Field];
}

/// <summary>
/// The relation kinds the index stores in <c>edges.rel</c>.
/// </summary>
/// <remarks>
/// <c>called_by</c> and <c>declares</c> are not here on purpose: the first is the same edge read backwards and the
/// second is implied by <see cref="ExtractedSymbol.Container"/>, so neither is worth a row. Storing a relation
/// twice is how a graph starts disagreeing with itself.
/// </remarks>
internal static class RelationKinds
{
    /// <summary>A call site, weighted by how many times it happens, capped.</summary>
    public const string Calls = "calls";

    /// <summary>A mention that is not a call: a type used in a signature, a constant, a generic argument.</summary>
    public const string References = "references";

    /// <summary>A base class.</summary>
    public const string Inherits = "inherits";

    /// <summary>An implemented interface.</summary>
    public const string Implements = "implements";

    /// <summary>An imported namespace, module or package.</summary>
    public const string Imports = "imports";

    /// <summary>A symbol named by another symbol's documentation.</summary>
    public const string DocMentions = "doc_mentions";
}
