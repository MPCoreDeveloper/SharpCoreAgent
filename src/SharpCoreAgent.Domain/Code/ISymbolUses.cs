namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// Answers "where else is this symbol used, as the compiler bound it?" for one language.
/// </summary>
/// <remarks>
/// The question is deliberately positional: the caller points at a token and the language service decides which
/// declaration that token means. That is the difference between this port and the index's name-based reference
/// search, which cannot tell two same-named members apart and does not pretend to.
/// </remarks>
public interface ISymbolUses
{
    /// <summary>
    /// Finds the uses of the symbol written at one position.
    /// </summary>
    /// <param name="path">A workspace-relative path to a source file this language claims.</param>
    /// <param name="line">The 1-based line of the symbol.</param>
    /// <param name="column">The 0-based column inside the symbol.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>
    /// The uses ordered by path, then line, then column; a failure when nothing is at the position or the project
    /// could not be loaded.
    /// </returns>
    ValueTask<SymbolUsesResult> FindUsesAsync(string path, int line, int column, CancellationToken cancellationToken);
}
