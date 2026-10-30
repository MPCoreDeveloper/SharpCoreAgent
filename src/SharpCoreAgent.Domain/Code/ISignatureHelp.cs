namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// Answers "what does this callable take?" for one language.
/// </summary>
/// <remarks>
/// Positional for the same reason as <see cref="ISymbolUses"/>: the signature that matters is the one the compiler
/// resolved at the call site, including the overload the argument types selected.
/// </remarks>
public interface ISignatureHelp
{
    /// <summary>
    /// Gets the signatures that apply at one position.
    /// </summary>
    /// <param name="path">A workspace-relative path to a source file this language claims.</param>
    /// <param name="line">The 1-based line of the call site.</param>
    /// <param name="column">The 0-based column of any character inside the name being called.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// The signatures that apply, most specific first, with the active one and the active argument named; a failure
    /// when nothing callable is at the position or the project could not be loaded.
    /// </returns>
    ValueTask<SignatureHelpResult> GetSignatureAsync(string path, int line, int column, CancellationToken cancellationToken);
}
