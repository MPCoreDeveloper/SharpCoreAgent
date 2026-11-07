namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// One framework cue the index found: a place where source text looks like a route, a test, a dependency-injection
/// registration or a database call.
/// </summary>
/// <remarks>
/// A cue is a <em>candidate</em>, not a fact: it comes from a regular expression over source text, so its
/// <see cref="Target"/> is the expression the author wrote (<c>app.get</c>) and never a resolved symbol. The reader
/// gets the citation (<c>path:line</c>) and the matched line, so a claim can be checked at the source itself.
/// </remarks>
public sealed record CodeCueRef
{
    /// <summary>Gets the cue kind, one of <see cref="CueKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the workspace-relative path of the file that holds the cue, with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the 1-based line the cue was found on.</summary>
    public required int Line { get; init; }

    /// <summary>Gets the written expression the cue names, for example <c>app.get</c> or <c>db.session.query</c>.</summary>
    public required string Target { get; init; }

    /// <summary>Gets the first quoted argument when the pattern captured one, such as a route path, or <see langword="null"/>.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets the declaration the enclosing passage opens, when it opens one, or <see langword="null"/>.</summary>
    public string? Symbol { get; init; }

    /// <summary>Gets the matched source line, trimmed and shortened, as the snippet a reader checks.</summary>
    public required string Snippet { get; init; }
}
