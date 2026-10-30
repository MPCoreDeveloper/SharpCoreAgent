namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// One place a symbol is written, as the compiler bound it.
/// </summary>
/// <remarks>
/// This is what a text search cannot produce: a use is a site the compiler resolved to the same declaration,
/// so shadowing, same-named members on other types and a string that merely spells the name are all excluded.
/// </remarks>
public sealed record SymbolUse
{
    /// <summary>
    /// Gets the workspace-relative path of the file the use appears in.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the 1-based line of the first character of the use.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// Gets the 0-based column of the first character of the use.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Gets a value indicating whether this site declares the symbol rather than referring to it.
    /// </summary>
    /// <remarks>
    /// The declaring site is part of the answer, not the header of it: a caller that lists definitions separately
    /// gets the same list from this flag, and nothing has to be searched twice.
    /// </remarks>
    public required bool IsDefinition { get; init; }

    /// <summary>
    /// Gets the symbol as the language service names it at this site.
    /// </summary>
    public required string Display { get; init; }
}
