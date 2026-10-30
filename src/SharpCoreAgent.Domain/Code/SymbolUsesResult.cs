namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// What a symbol-uses request answered: a list, or the reason there is none.
/// </summary>
/// <remarks>
/// Ordered by path, then line, then column, with the declaration first among uses from the same file. The order is
/// part of the contract because a transcript that is replayed has to produce the same tool answer, and a test
/// asserts the sequence rather than the membership.
/// </remarks>
public sealed record SymbolUsesResult
{
    /// <summary>
    /// Gets the uses, including the declaration when the project declares the symbol.
    /// </summary>
    public IReadOnlyList<SymbolUse> Uses { get; init; } = [];

    /// <summary>
    /// Gets the reason no uses were produced, or <see langword="null"/> on success.
    /// </summary>
    public CodeFailure? Failure { get; init; }

    /// <summary>
    /// Creates an answer that carries uses.
    /// </summary>
    /// <param name="uses">The uses, in the order the caller should print them.</param>
    /// <returns>A successful result.</returns>
    public static SymbolUsesResult Ok(IReadOnlyList<SymbolUse> uses) => new() { Uses = uses };

    /// <summary>
    /// Creates an answer that explains why it has no uses.
    /// </summary>
    /// <param name="failure">The reason.</param>
    /// <returns>A failed result with an empty list.</returns>
    public static SymbolUsesResult Failed(CodeFailure failure) => new() { Failure = failure };
}
