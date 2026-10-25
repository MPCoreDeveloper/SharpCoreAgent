using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The incremental write path: what a build adds, rewrites, drops and leaves alone, asserted through the rows it
/// left behind rather than through its own counters alone.
/// </summary>
/// <remarks>
/// Every test runs against a real SharpCoreDB file in a throwaway workspace, because the properties that matter —
/// a delete followed by an insert inside one batch, a subquery in a <c>DELETE</c>, a vector column that accepts a
/// <c>float[]</c> — are exactly the ones a mock would not exercise.
/// </remarks>
public sealed class IncrementalIndexTests
{
    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            public double Area(double radius) => Math.PI * radius * radius;
        }
        """;

    private const string ShapeSource = """
        namespace Demo.Geometry;

        public abstract class Shape
        {
            public abstract double Area();

            public double Twice(double radius) => Area() * 2;
        }
        """;

    private const string ProgramSource = """
        namespace Demo;

        public static class Program
        {
            public static string Greeting() => "hello";
        }
        """;

    private const string ReadmeSource = """
        # Demo

        A tiny workspace used by the incremental index tests.
        """;

    private const string ConfigSource = """
        {
          "name": "demo"
        }
        """;

    [Fact]
    public async Task IndexesEveryFileAndCountsThem()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(5, report.Added);
        Assert.Equal(0, report.Changed);
        Assert.Equal(0, report.Removed);
        Assert.False(report.Incomplete);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
        Assert.Equal(5, report.Status.Files);
        Assert.True(report.Status.Chunks >= 5, $"expected at least one passage per file, got {report.Status.Chunks}");
        Assert.True(report.Status.Symbols >= 4, $"expected the declarations of the corpus, got {report.Status.Symbols}");

        var meta = ReadMeta(sandbox.Workspace);
        Assert.Equal(IndexSchema.CompleteState, meta["build_state"]);
        Assert.Equal(26, meta["build_id"].Length);
        Assert.Equal("5", meta["file_count"]);
    }

    [Fact]
    public async Task SecondRunFindsNothingToDo()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        long chunks;

        await using (var first = SharpCoreDbIndexMaintainer.For(sandbox.Workspace))
        {
            var report = await first.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            chunks = report.Status.Chunks;
        }

        await using var second = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var again = await second.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // The whole point of the size/timestamp/hash comparison: a workspace nobody touched costs no writes.
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.Changed);
        Assert.Equal(5, again.Unchanged);
        Assert.Equal(chunks, again.Status.Chunks);
    }

    [Fact]
    public async Task OlderAnalyzerContractRewritesEveryFileAndThenReportsFresh()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using (var first = SharpCoreDbIndexMaintainer.For(sandbox.Workspace))
        {
            await first.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }

        // What an index built by the previous contract looks like: every file row still matches its file, so an
        // incremental pass would keep it and the vectors would stay in the old shape for ever.
        IndexTestDatabase.SetMeta(IndexPaths.For(sandbox.Workspace), "analyzer_contract", "2");

        await using var second = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await second.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(5, report.Added);
        Assert.Equal(0, report.Unchanged);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
        Assert.Equal("3", ReadMeta(sandbox.Workspace)["analyzer_contract"]);
    }

    [Fact]
    public async Task EditingOneFileRewritesOnlyThatFile()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        sandbox.Write("src/Program.cs", ProgramSource.Replace("hello", "bonjour", StringComparison.Ordinal));

        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Added);
        Assert.Equal(1, report.Changed);
        Assert.Equal(4, report.Unchanged);

        // The stored passage is the new text: a rewrite deletes the old rows rather than adding to them.
        var passages = ReadColumn(sandbox.Workspace, "SELECT text FROM chunks", "text");
        Assert.Contains(passages, text => text.Contains("bonjour", StringComparison.Ordinal));
        Assert.DoesNotContain(passages, text => text.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingAFileRemovesItsRows()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        File.Delete(sandbox.FullPath("src/Program.cs"));

        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Removed);
        Assert.Equal(4, report.Status.Files);
        Assert.DoesNotContain("src/Program.cs", ReadColumn(sandbox.Workspace, "SELECT path FROM files", "path"));
    }

    [Fact]
    public async Task SkippedFileIsRecordedWithItsReason()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Program.cs", ProgramSource);

        // A NUL byte in the head is what the scanner calls binary; the row it writes is the answer to "why is
        // this file not searchable?".
        sandbox.WriteBytes("assets/logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02]);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Status.SkippedFiles);
        Assert.Contains(report.Notes, note => note.StartsWith("assets/logo.png:", StringComparison.Ordinal));

        var states = ReadColumn(sandbox.Workspace, "SELECT state FROM files", "state");
        Assert.Contains(IndexFileStates.Skipped, states);
    }

    [Fact]
    public async Task RelationsAcrossFilesBecomeEdges()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("src/Usage.cs", """
            namespace Demo;

            public static class Usage
            {
                public static double Measure()
                {
                    var circle = new Geometry.Circle();
                    return circle.Area(2);
                }
            }
            """);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(report.Status.Edges > 0, "the call to Circle.Area should have become an edge");
        Assert.Contains("calls", ReadColumn(sandbox.Workspace, "SELECT rel FROM edges", "rel"));
    }

    [Fact]
    public async Task RebuildEmptiesTheTablesFirst()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.RebuildAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, report.Added);
        Assert.Equal(0, report.Changed);
        Assert.Equal(5, report.Status.Files);
        Assert.Equal(5, ReadColumn(sandbox.Workspace, "SELECT path FROM files", "path").Count);
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("src/Geometry/Shape.cs", ShapeSource);
        sandbox.Write("src/Program.cs", ProgramSource);
        sandbox.Write("docs/README.md", ReadmeSource);
        sandbox.Write("appsettings.json", ConfigSource);
    }

    private static Dictionary<string, string> ReadMeta(SharpCoreAgent.Domain.Workspace workspace)
    {
        var paths = IndexPaths.For(workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        return IndexSchema.ReadMeta(lease.Database.Database);
    }

    private static List<string> ReadColumn(SharpCoreAgent.Domain.Workspace workspace, string sql, string column)
    {
        var paths = IndexPaths.For(workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);

        var values = new List<string>();
        foreach (var row in IndexSql.Query(lease.Database.Database, sql))
        {
            values.Add(IndexSql.AsText(row.GetValueOrDefault(column)));
        }

        return values;
    }
}
