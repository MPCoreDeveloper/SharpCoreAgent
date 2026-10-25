using System.Globalization;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The budget a refresh runs under: a build that runs out of time stops, keeps what it indexed and says so.
/// </summary>
/// <remarks>
/// This is the property the turn-start refresh depends on. A caller waiting for an index before the first model
/// call needs a bound on how long that wait is, and the only honest answer is "as far as I got": rows already
/// written stay readable, the state is <c>partial</c>, and the next call continues.
/// </remarks>
public sealed class RefreshBudgetTests
{
    private const int FileCount = 50;

    [Fact]
    public async Task TinyBudgetStopsEarlyAndLeavesAUsableIndex()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);

        // Scanning fifty files already exceeds a millisecond, so the honest outcome is "nothing written yet".
        Assert.True(report.Incomplete);
        Assert.Equal(IndexFreshness.Partial, report.Status.Freshness);
        Assert.Contains(report.Notes, note => note.Contains("budget", StringComparison.Ordinal));

        // Usable means readable: the schema, the meta rows and whatever was written are all there.
        var meta = ReadMeta(sandbox.Workspace);
        Assert.Equal(IndexSchema.PartialState, meta["build_state"]);
        Assert.NotEqual(string.Empty, meta["build_id"]);
    }

    [Fact]
    public async Task SecondCallFinishesWhatTheBudgetCutShort()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var cut = await maintainer.EnsureFreshAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);
        Assert.True(cut.Incomplete);

        var finished = await maintainer.EnsureFreshAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.False(finished.Incomplete);
        Assert.Equal(FileCount, finished.Status.Files);
        Assert.Equal(FileCount, finished.Added + finished.Unchanged);
        Assert.Equal(IndexFreshness.Fresh, finished.Status.Freshness);
        Assert.Equal(IndexSchema.CompleteState, ReadMeta(sandbox.Workspace)["build_state"]);
    }

    [Fact]
    public async Task NoOpRefreshWritesNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var first = await maintainer.EnsureFreshAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var chunks = first.Status.Chunks;

        var second = await maintainer.EnsureFreshAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Changed);
        Assert.Equal(FileCount, second.Unchanged);
        Assert.Equal(chunks, second.Status.Chunks);
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        for (var index = 0; index < FileCount; index++)
        {
            var name = index.ToString("D2", CultureInfo.InvariantCulture);
            var source = $$"""
                namespace Demo.Units;

                public sealed class Unit{{name}}
                {
                    public int Value() => {{index}};
                }
                """;

            sandbox.Write("src/Unit" + name + ".cs", source);
        }
    }

    private static Dictionary<string, string> ReadMeta(SharpCoreAgent.Domain.Workspace workspace)
    {
        var paths = IndexPaths.For(workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        return IndexSchema.ReadMeta(lease.Database.Database);
    }
}
