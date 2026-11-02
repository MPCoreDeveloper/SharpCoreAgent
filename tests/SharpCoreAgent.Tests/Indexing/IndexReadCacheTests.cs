using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Retrieval;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// One loaded build per database per process: what it shares, what it refuses to share, and what it drops.
/// </summary>
/// <remarks>
/// The cache is process-wide state, so the class is serialized against the rest of the suite and each test starts
/// from an empty map. A test that counted entries while another class built an index in its own sandbox would be
/// measuring the suite rather than the cache.
/// </remarks>
[Collection(IndexReadCacheCollection.Name)]
public sealed class IndexReadCacheTests
{
    [Fact]
    public async Task TwoCallsForOneDatabaseShareOneLoadedBuild()
    {
        IndexReadCache.ForgetAll();
        using var sandbox = await IndexedAsync();
        var paths = IndexPaths.For(sandbox.Workspace);

        var first = IndexReadCache.For(paths);
        var second = IndexReadCache.For(paths);

        Assert.Same(first, second);
        Assert.Equal(1, IndexReadCache.EntryCount);

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task TwoDatabasesNeverShareABuild()
    {
        IndexReadCache.ForgetAll();
        using var one = await IndexedAsync();
        using var two = await IndexedAsync();

        var first = IndexReadCache.For(IndexPaths.For(one.Workspace));
        var second = IndexReadCache.For(IndexPaths.For(two.Workspace));

        Assert.NotSame(first, second);
        Assert.Equal(2, IndexReadCache.EntryCount);

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task TheShapeTheLeaseBuildsReadsOneBuild()
    {
        IndexReadCache.ForgetAll();
        using var sandbox = await IndexedAsync();
        var paths = IndexPaths.For(sandbox.Workspace);

        // `LeasedCodeIndex` builds exactly this — one instance per call, disposed afterwards — and the two instances
        // must read through one state, or every call would reload the whole index and recompute its status.
        using var first = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        using var second = SharpCoreDbCodeIndex.For(sandbox.Workspace);

        Assert.Same(first.State, second.State);
        Assert.Same(IndexReadCache.For(paths), first.State);
        Assert.Same(first.State.Reader, second.State.Reader);

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task ForgettingAWorkspaceStartsTheNextReaderEmpty()
    {
        IndexReadCache.ForgetAll();
        using var sandbox = await IndexedAsync();
        var paths = IndexPaths.For(sandbox.Workspace);
        var reader = IndexReadCache.For(paths);

        Assert.True(IndexReadCache.Forget(paths));
        Assert.False(IndexReadCache.Forget(paths));
        Assert.NotSame(reader, IndexReadCache.For(paths));

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task TheFifthDatabaseEvictsTheLeastRecentlyUsed()
    {
        IndexReadCache.ForgetAll();

        var sandboxes = new List<ToolTestWorkspace>(5);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                sandboxes.Add(await IndexedAsync());
            }

            var paths = sandboxes.Select(sandbox => IndexPaths.For(sandbox.Workspace)).ToArray();
            var readers = paths.Take(4).Select(IndexReadCache.For).ToArray();

            // The first workspace is asked for again, so the second is now the one nobody has touched.
            _ = IndexReadCache.For(paths[0]);
            _ = IndexReadCache.For(paths[4]);

            Assert.Equal(IndexReadCache.MaxEntries, IndexReadCache.EntryCount);
            Assert.Same(readers[0], IndexReadCache.For(paths[0]));
            Assert.NotSame(readers[1], IndexReadCache.For(paths[1]));
        }
        finally
        {
            foreach (var sandbox in sandboxes)
            {
                sandbox.Dispose();
            }

            IndexReadCache.ForgetAll();
        }
    }

    [Fact]
    public async Task AWorkspaceThatIsGoneIsDroppedRatherThanRemembered()
    {
        IndexReadCache.ForgetAll();

        var dead = await IndexedAsync();
        var deadPaths = IndexPaths.For(dead.Workspace);
        var reader = IndexReadCache.For(deadPaths);

        // The sandbox's temp directory takes the database with it, which is what every test in this suite does when
        // it finishes: a cache that kept those entries would grow for the length of a run.
        dead.Dispose();

        using var live = await IndexedAsync();

        Assert.Equal(1, IndexReadCache.EntryCount);
        Assert.NotSame(reader, IndexReadCache.For(deadPaths));

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task ANewInstanceSeesABuildMadeInThisProcess()
    {
        IndexReadCache.ForgetAll();
        using var sandbox = await IndexedAsync();

        using (var before = SharpCoreDbCodeIndex.For(sandbox.Workspace))
        {
            var answer = await before.SearchAsync(CodeQuery.Create("Square"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(answer, block => block.Path.EndsWith("Square.cs", StringComparison.Ordinal));
        }

        sandbox.Write("src/Geometry/Square.cs", SquareSource);
        await BuildAsync(sandbox);

        // A new instance, and the same shared reader: the snapshot is re-keyed by build_id rather than held by a
        // promise, which is the property that lets it outlive the call at all.
        using (var after = SharpCoreDbCodeIndex.For(sandbox.Workspace))
        {
            var answer = await after.SearchAsync(CodeQuery.Create("Square"), TestContext.Current.CancellationToken);
            Assert.Contains(answer, block => block.Path.EndsWith("Square.cs", StringComparison.Ordinal));
        }

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task AStatusComputedByOneInstanceIsReadByTheNext()
    {
        IndexReadCache.ForgetAll();
        using var sandbox = await IndexedAsync();
        var paths = IndexPaths.For(sandbox.Workspace);

        // Every tool answers with the index's freshness in its heading, so the status is on the served call's path
        // as surely as the snapshot is: 1 347 ms to compute on this repository's index, and it was paid per call
        // for as long as the memo belonged to the instance.
        using (var first = SharpCoreDbCodeIndex.For(sandbox.Workspace))
        {
            _ = await first.GetStatusAsync(TestContext.Current.CancellationToken);
        }

        var memo = IndexReadCache.For(paths).Status;
        Assert.NotNull(memo);

        using (var second = SharpCoreDbCodeIndex.For(sandbox.Workspace))
        {
            var status = await second.GetStatusAsync(TestContext.Current.CancellationToken);

            Assert.Same(memo.Status, status);
            Assert.Equal(1, status.Files);
        }

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task AStatusIsRecomputedWhenTheFileChanges()
    {
        IndexReadCache.ForgetAll();
        using var sandbox = await IndexedAsync();

        using (var first = SharpCoreDbCodeIndex.For(sandbox.Workspace))
        {
            Assert.Equal(1, (await first.GetStatusAsync(TestContext.Current.CancellationToken)).Files);
        }

        sandbox.Write("src/Geometry/Square.cs", SquareSource);
        await BuildAsync(sandbox);

        // The memo is keyed by the file's stamp, so a build in this process — as in any other — is seen by the next
        // read: a cache that answered "1 file" here would be the stale server this round must not build.
        using (var second = SharpCoreDbCodeIndex.For(sandbox.Workspace))
        {
            Assert.Equal(2, (await second.GetStatusAsync(TestContext.Current.CancellationToken)).Files);
        }

        IndexReadCache.ForgetAll();
    }

    [Fact]
    public async Task InvalidatingTheStatusFromOneInstanceDropsItForEveryReader()
    {
        IndexReadCache.ForgetAll();
        using var sandbox = await IndexedAsync();
        var paths = IndexPaths.For(sandbox.Workspace);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        _ = await index.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(IndexReadCache.For(paths).Status);

        index.InvalidateStatus();

        Assert.Null(IndexReadCache.For(paths).Status);

        IndexReadCache.ForgetAll();
    }

    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            public double Area() => 3.14;
        }
        """;

    private const string SquareSource = """
        namespace Demo.Geometry;

        public sealed class Square
        {
            public double Area() => 4;
        }
        """;

    /// <summary>Creates a sandbox holding one built index, which is what the cache needs to exist.</summary>
    private static async Task<ToolTestWorkspace> IndexedAsync()
    {
        var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        await BuildAsync(sandbox);
        return sandbox;
    }

    private static async Task BuildAsync(ToolTestWorkspace sandbox)
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
    }
}

/// <summary>Serializes the tests that read the process-wide reader cache.</summary>
/// <remarks>
/// The map is static, so two classes asserting its size at once would assert about each other. The collection is
/// what keeps them apart without making the whole suite single-threaded.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IndexReadCacheCollection
{
    private IndexReadCacheCollection()
    {
    }

    /// <summary>Gets the collection name.</summary>
    public const string Name = "index-read-cache";
}
