using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Infrastructure.McpServer;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The promise a long-lived server makes to the rest of the machine: the index is open for one read and closed
/// afterwards, so a build in another process is never waiting on an idle editor.
/// </summary>
/// <remarks>
/// The shape the handle's lifetime takes is asserted here with a fake: one open per operation, a release on every
/// exit path. The last test uses the real index instead, because the property a developer feels — a build in
/// another process is never waiting on an idle server — is the operating system's, not a fake's.
/// </remarks>
public sealed class LeasedCodeIndexTests
{
    [Fact]
    public async Task ALeasedReadLeavesNoLockBehindForABuild()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", "namespace Demo; public sealed class Circle { public double Circumference() => 6.28; }");

        var index = new LeasedCodeIndex(() => SharpCoreAgentIndex.Open(sandbox.Workspace));
        await index.GetStatusAsync(Token);
        await index.SearchAsync(CodeQuery.Create("Circumference"), Token);

        // The leased view is still alive and still answering, and the writer lock is free: nothing is holding the
        // database between two calls, which is what lets `sharpcoreagent index` run in a terminal beside an editor.
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(30), Token);

        Assert.NotEqual(IndexFreshness.Building, report.Status.Freshness);
        Assert.False(report.Incomplete);
    }

    [Fact]
    public async Task EachReadOpensAndReleasesItsOwnHandle()
    {
        var handles = new List<FakeIndex>();
        var index = new LeasedCodeIndex(() => Opened(handles));

        await index.GetStatusAsync(Token);
        await index.SearchAsync(CodeQuery.Create("circumference"), Token);

        Assert.Equal(2, handles.Count);
        Assert.All(handles, handle => Assert.True(handle.Released));
    }

    [Fact]
    public async Task EveryPortOfTheIndexIsLeased()
    {
        var handles = new List<FakeIndex>();
        var index = new LeasedCodeIndex(() => Opened(handles));

        await index.GetStatusAsync(Token);
        await index.SearchAsync(CodeQuery.Create("x"), Token);
        await index.FindSymbolsAsync("Circle", kind: null, path: null, language: null, evidenceKind: null, Token);
        await index.FindReferencesAsync("Circle", ReferenceDirection.Callers, maxResults: 5, Token);
        await index.GetContextAsync("src/Circle.cs", hops: 1, k: 3, Token);

        // Five operations, five handles: nothing is cached, because a cached status would describe an index this
        // call may no longer be reading.
        Assert.Equal(5, handles.Count);
        Assert.All(handles, handle => Assert.True(handle.Released));
    }

    [Fact]
    public async Task AReadThatFailsStillReleasesTheHandle()
    {
        var handles = new List<FakeIndex>();
        var index = new LeasedCodeIndex(() => Opened(handles, new InvalidOperationException("the file is corrupt")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => index.GetStatusAsync(Token).AsTask());

        Assert.True(Assert.Single(handles).Released);
    }

    [Fact]
    public async Task AReadThatIsCancelledStillReleasesTheHandle()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var handles = new List<FakeIndex>();
        var index = new LeasedCodeIndex(() => Opened(handles, new OperationCanceledException(cancelled.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.SearchAsync(CodeQuery.Create("x"), cancelled.Token).AsTask());

        Assert.True(Assert.Single(handles).Released);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FakeIndex Opened(List<FakeIndex> handles, Exception? failure = null)
    {
        var handle = new FakeIndex(failure);
        handles.Add(handle);
        return handle;
    }

    /// <summary>The read port, as a handle that says when it was closed.</summary>
    private sealed class FakeIndex(Exception? failure) : ICodeIndex, IAsyncDisposable
    {
        public bool Released { get; private set; }

        public ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Answer(IndexStatus.Missing("C:\\work"));

        public ValueTask<IReadOnlyList<CodeEvidence>> SearchAsync(CodeQuery query, CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<CodeEvidence>>([]);

        public ValueTask<IReadOnlyList<CodeSymbolRef>> FindSymbolsAsync(
            string name,
            string? kind,
            string? path,
            string? language,
            string? evidenceKind,
            CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<CodeSymbolRef>>([]);

        public ValueTask<IReadOnlyList<CodeEvidence>> FindReferencesAsync(
            string symbol,
            ReferenceDirection direction,
            int maxResults,
            CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<CodeEvidence>>([]);

        public ValueTask<IReadOnlyList<CodeDocRef>> FindDocsAsync(
            string text,
            int maxResults,
            CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<CodeDocRef>>([]);

        public ValueTask<IReadOnlyList<CodeCueRef>> FindCuesAsync(
            string kind,
            string? text,
            int maxResults,
            CancellationToken cancellationToken) =>
            Answer<IReadOnlyList<CodeCueRef>>([]);

        public ValueTask<CodeContext> GetContextAsync(
            string target,
            int hops,
            int k,
            CancellationToken cancellationToken) =>
            Answer(CodeContext.Empty(target));

        public ValueTask DisposeAsync()
        {
            Released = true;
            return ValueTask.CompletedTask;
        }

        private ValueTask<T> Answer<T>(T value) =>
            failure is null ? ValueTask.FromResult(value) : ValueTask.FromException<T>(failure);
    }
}
