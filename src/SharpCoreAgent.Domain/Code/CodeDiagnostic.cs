namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// One diagnostic a language service reported for one position.
/// </summary>
/// <remarks>
/// <see cref="Line"/> is 1-based and <see cref="Column"/> is 0-based, which is the convention every language
/// service in this repository's reach uses and the one an editor puts in a link. A tool prints a position as
/// <c>path:line:column</c> and adjusts neither number, so a reader can paste it into an editor unchanged.
/// </remarks>
public sealed record CodeDiagnostic
{
    /// <summary>
    /// Gets how serious the problem is.
    /// </summary>
    public required CodeSeverity Severity { get; init; }

    /// <summary>
    /// Gets the compiler's own identifier for the problem, for example <c>FS0039</c>.
    /// </summary>
    /// <remarks>
    /// Kept as the compiler spells it rather than as a number of our own: a model that has read a compiler error
    /// before recognises the id, and so does the documentation it can look up.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the workspace-relative path of the file the diagnostic belongs to.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the 1-based line of the first character.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// Gets the 0-based column of the first character.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Gets the message, flattened to a single line.
    /// </summary>
    public required string Message { get; init; }
}
