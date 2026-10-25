namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// One declaration the index knows about.
/// </summary>
/// <remarks>
/// Symbols are the join between retrieval and the graph: a passage can be cited by line range, but a
/// declaration is what callers, callees and communities attach to. <see cref="Key"/> is stable across
/// builds because it is derived from the name and the kind, not from a row id, so a tool can hand a key
/// back and get the same declaration after the index is rebuilt.
/// </remarks>
public sealed record CodeSymbolRef
{
    /// <summary>
    /// Gets the stable key: <c>&lt;namespace&gt;.&lt;type&gt;[.&lt;member&gt;]#&lt;kind&gt;</c>.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the short name, without namespace or declaring type.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the dotted qualified name, without the <c>#kind</c> suffix.
    /// </summary>
    public required string QualifiedName { get; init; }

    /// <summary>
    /// Gets the declaration kind: <c>class</c>, <c>interface</c>, <c>record</c>, <c>struct</c>, <c>enum</c>,
    /// <c>method</c>, <c>property</c> or <c>field</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the workspace-relative path of the file that declares it.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the first line of the declaration, 1-based and inclusive.
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// Gets the last line of the declaration, 1-based and inclusive.
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// Gets the declaration line as written, when the language gives one.
    /// </summary>
    public string? Signature { get; init; }

    /// <summary>
    /// Gets the documentation summary attached to the declaration, flattened to plain text.
    /// </summary>
    public string? Doc { get; init; }

    /// <summary>
    /// Gets how many indexed call sites point at this declaration.
    /// </summary>
    public int Callers { get; init; }

    /// <summary>
    /// Gets how many indexed call sites this declaration has.
    /// </summary>
    public int Callees { get; init; }

    /// <summary>
    /// Gets the community label assigned by the last build, for example <c>c12</c>.
    /// </summary>
    public string? Community { get; init; }
}
