namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// Read side of the index: everything the five retrieval tools need, and nothing that mutates.
/// </summary>
/// <remarks>
/// Two obligations are part of the interface, not of one adapter's manners:
/// <list type="number">
/// <item>Absence is a value. A missing, disabled, corrupt or locked index returns an empty list and a
/// <see cref="GetStatusAsync"/> that names the state; the tools turn "empty and not fresh" into the error
/// the model reads.</item>
/// <item>Same index plus same query gives the same list in the same order, so a transcript can be
/// replayed and a test can assert order instead of membership.</item>
/// </list>
/// Implementations bound every list they return, even where the caller already does.
/// </remarks>
public interface ICodeIndex
{
    /// <summary>
    /// Reports what the index is and how healthy it is.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The current status; <see cref="IndexStatus.Missing(string)"/> when the workspace has never been
    /// indexed, <see cref="IndexFreshness.Corrupt"/> when it was rejected on open. Never throws for absence.
    /// </returns>
    ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Finds the passages that answer one query.
    /// </summary>
    /// <param name="query">A validated request from <see cref="CodeQuery.Create"/>.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// At most <see cref="CodeQuery.K"/> blocks, best first; an empty list when the index is absent or has
    /// nothing to say.
    /// </returns>
    ValueTask<IReadOnlyList<CodeEvidence>> SearchAsync(CodeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Finds declarations by name.
    /// </summary>
    /// <param name="name">A short name, a qualified name or a fragment of one; matched case-insensitively.</param>
    /// <param name="kind">A declaration kind to restrict to, or <see langword="null"/> for all of them.</param>
    /// <param name="path">A workspace-relative file or directory to restrict to, or <see langword="null"/>.</param>
    /// <param name="language">An indexed language to restrict to, or <see langword="null"/> for every language.</param>
    /// <param name="evidenceKind">A coarse evidence kind (<c>code</c>/<c>document</c>/<c>configuration</c>/<c>other</c>) or
    /// <see langword="null"/> for all of them.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Declarations ordered by exactness of the match, then by path and line; possibly empty.</returns>
    ValueTask<IReadOnlyList<CodeSymbolRef>> FindSymbolsAsync(
        string name,
        string? kind,
        string? path,
        string? language,
        string? evidenceKind,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the passages that call, are called by, or otherwise relate to one declaration.
    /// </summary>
    /// <param name="symbol">An exact or qualified symbol name.</param>
    /// <param name="direction">Which side of the relation to follow.</param>
    /// <param name="maxResults">The hard cap on returned blocks; implementations may cap it lower.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Blocks labelled with the relation that reached them; possibly empty.</returns>
    ValueTask<IReadOnlyList<CodeEvidence>> FindReferencesAsync(
        string symbol,
        ReferenceDirection direction,
        int maxResults,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds documentation sections whose heading or text matches one phrase.
    /// </summary>
    /// <param name="text">Words or a phrase; a section matches when every whitespace-separated term appears in it.</param>
    /// <param name="maxResults">The hard cap on returned sections; implementations may cap it lower.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// Sections ordered by heading match, then by path and line; possibly empty. Each carries the declaration its
    /// heading names, when the build could attach one.
    /// </returns>
    ValueTask<IReadOnlyList<CodeDocRef>> FindDocsAsync(string text, int maxResults, CancellationToken cancellationToken);

    /// <summary>
    /// Finds framework cues — routes, tests, dependency-injection registrations and database calls — in the source
    /// text the index already holds, as cited candidates.
    /// </summary>
    /// <param name="kind">One of <see cref="CueKinds"/>, case-insensitive.</param>
    /// <param name="text">An optional term the cue's target, path or captured detail must contain.</param>
    /// <param name="maxResults">The hard cap on returned cues; implementations may cap it lower.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// The cues, ordered by path then line; possibly empty. Each is a candidate — a regular-expression hit over
    /// source — and names the written expression rather than a resolved symbol.
    /// </returns>
    ValueTask<IReadOnlyList<CodeCueRef>> FindCuesAsync(
        string kind,
        string? text,
        int maxResults,
        CancellationToken cancellationToken);

    /// <summary>
    /// Explains what sits around one file or declaration.
    /// </summary>
    /// <param name="target">A workspace-relative path, or a symbol name.</param>
    /// <param name="hops">How many graph hops to follow; 1 to 3.</param>
    /// <param name="k">How many neighbours to return; 1 to 12.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// The centre, its own passage and the neighbouring blocks, nearest first; an empty context when the target
    /// names nothing the index holds.
    /// </returns>
    /// <remarks>
    /// A context is a record rather than a bare list because the answer's heading names the declaration the
    /// neighbours belong to, and a list of neighbours alone cannot say which one that was.
    /// </remarks>
    ValueTask<CodeContext> GetContextAsync(string target, int hops, int k, CancellationToken cancellationToken);
}
