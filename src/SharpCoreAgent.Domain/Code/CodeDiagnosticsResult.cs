namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// What a diagnostics request answered: a list, or the reason there is none.
/// </summary>
/// <remarks>
/// Never both. <see cref="Failure"/> is <see langword="null"/> on success and <see cref="Diagnostics"/> is empty
/// on failure, so a caller tests one field instead of two. A clean file is therefore a success with an empty
/// list, which is a different answer from "the check could not run".
/// </remarks>
public sealed record CodeDiagnosticsResult
{
    /// <summary>
    /// Gets the diagnostics, ordered by line, then column.
    /// </summary>
    /// <remarks>
    /// One file's worth, so the order is positional and stable; a caller may print the list as it arrives.
    /// </remarks>
    public IReadOnlyList<CodeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets the reason no diagnostics were produced, or <see langword="null"/> on success.
    /// </summary>
    public CodeFailure? Failure { get; init; }

    /// <summary>
    /// Creates an answer that carries diagnostics.
    /// </summary>
    /// <param name="diagnostics">The diagnostics, in the order the caller should print them.</param>
    /// <returns>A successful result.</returns>
    public static CodeDiagnosticsResult Ok(IReadOnlyList<CodeDiagnostic> diagnostics) =>
        new() { Diagnostics = diagnostics };

    /// <summary>
    /// Creates an answer that explains why it has no diagnostics.
    /// </summary>
    /// <param name="failure">The reason.</param>
    /// <returns>A failed result with an empty list.</returns>
    public static CodeDiagnosticsResult Failed(CodeFailure failure) => new() { Failure = failure };
}
