using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// An <see cref="ICodeIndex"/> that opens the index for one operation and lets go of it afterwards.
/// </summary>
/// <remarks>
/// A server an editor keeps open for an eight-hour session must not hold the database while it is idle: the
/// handle a query leaves behind is what makes <c>sharpcoreagent index</c> in a terminal wait or fail. The
/// factory is called per operation and what it returns is disposed in a <c>finally</c>, so the file is closed
/// between two calls. One file open per call is the price, and the index's own latency budget is what keeps
/// that price honest.
/// <para>
/// Nothing is cached here for the same reason: a status read by an earlier call would describe an index this
/// call may no longer be reading, and a server whose answers went stale because of a cache would be worse than
/// one that is merely slow.
/// </para>
/// </remarks>
public sealed class LeasedCodeIndex : ICodeIndex
{
    private readonly Func<ICodeIndex> _open;

    /// <summary>
    /// Creates a view that opens the index per operation.
    /// </summary>
    /// <param name="open">Opens one index handle; the object it returns is disposed when the operation ends.</param>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> is null.</exception>
    public LeasedCodeIndex(Func<ICodeIndex> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
    }

    /// <inheritdoc />
    public ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        UseAsync((index, token) => index.GetStatusAsync(token), cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeEvidence>> SearchAsync(CodeQuery query, CancellationToken cancellationToken) =>
        UseAsync((index, token) => index.SearchAsync(query, token), cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeSymbolRef>> FindSymbolsAsync(
        string name,
        string? kind,
        string? path,
        string? language,
        string? evidenceKind,
        CancellationToken cancellationToken) =>
        UseAsync((index, token) => index.FindSymbolsAsync(name, kind, path, language, evidenceKind, token), cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeEvidence>> FindReferencesAsync(
        string symbol,
        ReferenceDirection direction,
        int maxResults,
        CancellationToken cancellationToken) =>
        UseAsync((index, token) => index.FindReferencesAsync(symbol, direction, maxResults, token), cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeDocRef>> FindDocsAsync(
        string text,
        int maxResults,
        CancellationToken cancellationToken) =>
        UseAsync((index, token) => index.FindDocsAsync(text, maxResults, token), cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeCueRef>> FindCuesAsync(
        string kind,
        string? text,
        int maxResults,
        CancellationToken cancellationToken) =>
        UseAsync((index, token) => index.FindCuesAsync(kind, text, maxResults, token), cancellationToken);

    /// <inheritdoc />
    public ValueTask<CodeContext> GetContextAsync(
        string target,
        int hops,
        int k,
        CancellationToken cancellationToken) =>
        UseAsync((index, token) => index.GetContextAsync(target, hops, k, token), cancellationToken);

    /// <summary>
    /// Runs one operation against a freshly opened index and releases it afterwards.
    /// </summary>
    /// <typeparam name="T">What the operation answers with.</typeparam>
    /// <param name="operation">The read to perform.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The operation's answer.</returns>
    /// <remarks>
    /// The release runs even when the read failed or was cancelled: an exception escaping with the file still
    /// open would be the one bug this type exists to make impossible.
    /// </remarks>
    private async ValueTask<T> UseAsync<T>(
        Func<ICodeIndex, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        var index = _open();
        try
        {
            return await operation(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync(index).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes one handle, whichever disposal it offers.
    /// </summary>
    /// <param name="index">The handle this call opened.</param>
    /// <returns>A task that completes when the handle is closed.</returns>
    /// <remarks>
    /// <see cref="ICodeIndex"/> is a read port and says nothing about disposal, so the two shapes are both
    /// accepted: the shipped index closes its database asynchronously, and a test double may close nothing.
    /// </remarks>
    private static async ValueTask ReleaseAsync(ICodeIndex index)
    {
        if (index is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync().ConfigureAwait(false);
        }
        else if (index is IDisposable synchronous)
        {
            synchronous.Dispose();
        }
    }
}
