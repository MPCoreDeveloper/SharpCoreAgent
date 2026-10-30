namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// One callable's signature, as it would be shown at a call site.
/// </summary>
/// <remarks>
/// <see cref="Display"/> is the language's own rendering of the declaration, because a hand-built rendering of a
/// curried F# function is where the type variables get lost. <see cref="Parameters"/> is the same rendering cut
/// into arguments, so a caller can point at one without re-parsing the line.
/// </remarks>
public sealed record SignatureInfo
{
    /// <summary>
    /// Gets the whole signature on one line, for example <c>val total: items: Item list -&gt; decimal</c>.
    /// </summary>
    public required string Display { get; init; }

    /// <summary>
    /// Gets the arguments in declaration order, each rendered as <c>name: type</c>.
    /// </summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary>
    /// Gets the 0-based index of the argument the cursor is on, or <c>-1</c> when the position is not inside a call.
    /// </summary>
    /// <remarks>
    /// Computed from the call site rather than reported by the language service, which exposes no such index; a
    /// position outside an argument list therefore says so instead of guessing the first parameter.
    /// </remarks>
    public required int ActiveParameter { get; init; }
}
