namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// What a signature request answered: the overloads at a call site, or the reason there are none.
/// </summary>
/// <remarks>
/// Plural because an overloaded call site has more than one honest answer. <see cref="ActiveSignature"/> says
/// which of them the position resolves against, so a caller that prints only one line has a rule for choosing it
/// instead of taking the first.
/// </remarks>
public sealed record SignatureHelpResult
{
    /// <summary>
    /// Gets the signatures that apply at the position, most specific first.
    /// </summary>
    public IReadOnlyList<SignatureInfo> Signatures { get; init; } = [];

    /// <summary>
    /// Gets the 0-based index into <see cref="Signatures"/> that the position resolves to, or <c>-1</c> when empty.
    /// </summary>
    public int ActiveSignature { get; init; }

    /// <summary>
    /// Gets the reason no signatures were produced, or <see langword="null"/> on success.
    /// </summary>
    public CodeFailure? Failure { get; init; }

    /// <summary>
    /// Creates an answer that carries signatures.
    /// </summary>
    /// <param name="signatures">The signatures that apply.</param>
    /// <param name="activeSignature">The index the position resolves to, or <c>-1</c>.</param>
    /// <returns>A successful result.</returns>
    public static SignatureHelpResult Ok(IReadOnlyList<SignatureInfo> signatures, int activeSignature) =>
        new() { Signatures = signatures, ActiveSignature = activeSignature };

    /// <summary>
    /// Creates an answer that explains why it has no signatures.
    /// </summary>
    /// <param name="failure">The reason.</param>
    /// <returns>A failed result with an empty list.</returns>
    public static SignatureHelpResult Failed(CodeFailure failure) => new() { Failure = failure };
}
