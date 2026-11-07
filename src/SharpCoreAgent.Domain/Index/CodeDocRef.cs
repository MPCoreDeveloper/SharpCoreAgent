namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// One documentation section the index holds: where it is, what it is headed, and the declaration it describes.
/// </summary>
/// <remarks>
/// A section is a Markdown heading block or a symbol's doc comment. <see cref="Symbol"/> is the qualified name of the
/// declaration the section describes — the Markdown heading that names a symbol, mapped at build time — or
/// <see langword="null"/> when the prose describes nothing the index holds. That link is the whole point: it turns
/// "the docs mention <c>Total</c>" into a citation a reader can open beside the declaration itself.
/// </remarks>
public sealed record CodeDocRef
{
    /// <summary>Gets the workspace-relative path of the file that holds the section, with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the heading the section sits under, without <c>#</c> markers, or <see langword="null"/>.</summary>
    public string? Heading { get; init; }

    /// <summary>Gets the qualified name of the declaration the section describes, or <see langword="null"/>.</summary>
    public string? Symbol { get; init; }

    /// <summary>Gets the 1-based first line of the section, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line of the section, inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the section text exactly as it was indexed.</summary>
    public required string Text { get; init; }
}
