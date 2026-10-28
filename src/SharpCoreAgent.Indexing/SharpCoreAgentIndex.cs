using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The one handle a host application needs: the read side the tools receive and the write side the CLI drives.
/// </summary>
/// <remarks>
/// The two halves live on separate interfaces on purpose — <see cref="ICodeIndex"/> is handed to tools, and
/// <see cref="ICodeIndexMaintainer"/> never is — but they share one index file, one writer lock and one process-wide
/// database handle. Exposing them through a single object is what keeps a host from accidentally opening the index
/// twice, while still letting the container give a tool the read-only view.
/// <para>
/// This type lives in the indexing project rather than in dependency injection because it is the library's public
/// entry point: a test can open an index without a host, and a future adapter (a different engine, a remote index)
/// has one place to slot into.
/// </para>
/// </remarks>
public sealed class SharpCoreAgentIndex : ICodeIndex, ICodeIndexMaintainer, IDisposable, IAsyncDisposable
{
    private readonly SharpCoreDbCodeIndex _reader;
    private readonly SharpCoreDbIndexMaintainer _writer;

    private SharpCoreAgentIndex(SharpCoreDbCodeIndex reader, SharpCoreDbIndexMaintainer writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <summary>
    /// Gets the directory the index keeps its database, lock and temporary files in.
    /// </summary>
    /// <remarks>
    /// Exposed for diagnostics: <c>sharpagent index status</c> prints it, and a developer wondering where a build
    /// went can look without guessing the workspace convention.
    /// </remarks>
    public string IndexDirectory => _reader.Paths.IndexDirectory;

    /// <summary>
    /// Gets the workspace root the index describes.
    /// </summary>
    public string WorkspaceRoot => _reader.Paths.WorkspaceRoot;

    /// <summary>
    /// Opens the index of one workspace.
    /// </summary>
    /// <param name="workspace">The workspace whose index is read and built.</param>
    /// <param name="encryptionPassword">A password when the index is encrypted; otherwise <see langword="null"/>.</param>
    /// <returns>The index handle, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    /// <remarks>
    /// Nothing is created and nothing is read here: the first method call decides whether the index exists. That keeps
    /// <c>SharpCoreAgentIndex.Open</c> safe to call from a host that will never index anything.
    /// </remarks>
    public static SharpCoreAgentIndex Open(Workspace workspace, string? encryptionPassword = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new SharpCoreAgentIndex(
            SharpCoreDbCodeIndex.For(workspace, encryptionPassword),
            SharpCoreDbIndexMaintainer.For(workspace, encryptionPassword));
    }

    /// <inheritdoc />
    public ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        _reader.GetStatusAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeEvidence>> SearchAsync(CodeQuery query, CancellationToken cancellationToken) =>
        _reader.SearchAsync(query, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeSymbolRef>> FindSymbolsAsync(
        string name,
        string? kind,
        CancellationToken cancellationToken) =>
        _reader.FindSymbolsAsync(name, kind, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeEvidence>> FindReferencesAsync(
        string symbol,
        ReferenceDirection direction,
        int maxResults,
        CancellationToken cancellationToken) =>
        _reader.FindReferencesAsync(symbol, direction, maxResults, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CodeContext> GetContextAsync(
        string target,
        int hops,
        int k,
        CancellationToken cancellationToken) =>
        _reader.GetContextAsync(target, hops, k, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IndexBuildReport> EnsureFreshAsync(TimeSpan budget, CancellationToken cancellationToken) =>
        _writer.EnsureFreshAsync(budget, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IndexBuildReport> RebuildAsync(CancellationToken cancellationToken) =>
        _writer.RebuildAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IndexVerification> VerifyAsync(CancellationToken cancellationToken) =>
        _writer.VerifyAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IndexBuildReport> PruneAsync(CancellationToken cancellationToken) =>
        _writer.PruneAsync(cancellationToken);

    /// <summary>
    /// Closes the reader's handle. The writer's lock is released by its own asynchronous disposal.
    /// </summary>
    public void Dispose() => _reader.Dispose();

    /// <summary>
    /// Closes both halves.
    /// </summary>
    /// <returns>A task that completes when the writer has released the lock.</returns>
    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
