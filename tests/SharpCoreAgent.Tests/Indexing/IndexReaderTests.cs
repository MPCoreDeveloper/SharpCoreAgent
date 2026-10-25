using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Retrieval;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The snapshot a query answers from: loaded once per build, reused while that build is current, reloaded when a
/// rebuild changes the id.
/// </summary>
/// <remarks>
/// The counts are compared against the tables themselves rather than against the build report, because the point
/// of the snapshot is that retrieval never sees the database again during a query.
/// </remarks>
public sealed class IndexReaderTests
{
    [Fact]
    public async Task SnapshotMatchesTheTables()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var lease = Open(sandbox);
        var snapshot = IndexReader.Create().Load(lease.Database.Database);

        Assert.Equal(3, snapshot.Paths.Count);
        Assert.Equal(Count(sandbox, "files"), snapshot.Paths.Count);
        Assert.Equal(ReadChunkCount(sandbox), snapshot.ChunkCount);
        Assert.Equal(ReadSymbolCount(sandbox), snapshot.SymbolCount);
        Assert.Equal("hashed-local/512", snapshot.Embedder);
        Assert.Equal(512, snapshot.Dimensions);
        Assert.True(snapshot.HasVectors);
        Assert.Contains("src/Geometry/Circle.cs", snapshot.Paths.Values);
    }

    [Fact]
    public async Task SecondLoadReusesTheSnapshot()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var lease = Open(sandbox);
        var reader = IndexReader.Create();

        var first = reader.Load(lease.Database.Database);
        var second = reader.Load(lease.Database.Database);

        // One load per build: the second call reads build_id and nothing else.
        Assert.Same(first, second);
    }

    [Fact]
    public async Task NewBuildProducesANewSnapshot()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var lease = Open(sandbox);
        var reader = IndexReader.Create();
        var before = reader.Load(lease.Database.Database);

        sandbox.Write("src/Geometry/Circle.cs", CircleSource.Replace("3.14", "3.14159", StringComparison.Ordinal));
        await BuildAsync(sandbox);

        var after = reader.Load(lease.Database.Database);

        Assert.NotSame(before, after);
        Assert.NotEqual(before.BuildId, after.BuildId);
        Assert.Contains(after.Chunks.Values, chunk => chunk.Text.Contains("3.14159", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SnapshotCarriesCitations()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var lease = Open(sandbox);
        var snapshot = IndexReader.Create().Load(lease.Database.Database);

        var chunk = Assert.Single(
            snapshot.Chunks.Values,
            row => row.Path.EndsWith("Circle.cs", StringComparison.Ordinal));

        Assert.Equal(8, chunk.Sha8.Length);
        Assert.True(chunk.EndLine >= chunk.StartLine);
        Assert.Contains("Circle", chunk.Text, StringComparison.Ordinal);
    }

    private const string ShapeSource = """
        namespace Demo.Geometry;

        public abstract class Shape
        {
            public abstract double Area();
        }
        """;

    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle : Shape
        {
            public override double Area() => 3.14;
        }
        """;

    private const string ReadmeSource = """
        # Geometry

        How the shapes of this workspace relate to one another.
        """;

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Shape.cs", ShapeSource);
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("docs/geometry.md", ReadmeSource);
    }

    private static async Task BuildAsync(ToolTestWorkspace sandbox)
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
    }

    private static IndexHandleLease Open(ToolTestWorkspace sandbox) =>
        IndexHandleCache.Acquire(IndexPaths.For(sandbox.Workspace), encryptionPassword: null, createIfMissing: false);

    private static long Count(ToolTestWorkspace sandbox, string table)
    {
        using var lease = Open(sandbox);
        return IndexSql.Count(lease.Database.Database, table switch
        {
            "files" => IndexTable.Files,
            "chunks" => IndexTable.Chunks,
            "symbols" => IndexTable.Symbols,
            _ => IndexTable.Edges,
        });
    }

    private static int ReadChunkCount(ToolTestWorkspace sandbox) => (int)Count(sandbox, "chunks");

    private static int ReadSymbolCount(ToolTestWorkspace sandbox) => (int)Count(sandbox, "symbols");
}
