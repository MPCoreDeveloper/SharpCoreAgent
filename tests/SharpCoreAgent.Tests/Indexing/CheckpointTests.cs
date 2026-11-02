using System.Globalization;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Extraction;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The checkpoint: how many times a build asks the store to commit, and what a group that is abandoned leaves behind.
/// </summary>
/// <remarks>
/// The count is the property, not the clock. D21 priced a commit at 1.36 s on this repository's index, so "one commit
/// per file" is a number of commits before it is a number of seconds, and a test can assert that on five files in a way
/// it could never assert a duration (spec 026, D7).
/// <para>
/// Every test drives a real SharpCoreDB file, because what is asserted — that <c>CancelBatchUpdate</c> discards a group
/// and <c>EndBatchUpdate</c> publishes one — is the engine's behaviour rather than this repository's bookkeeping.
/// </para>
/// </remarks>
public sealed class CheckpointTests
{
    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            public double Area(double radius) => Math.PI * radius * radius;
        }
        """;

    private const string SquareSource = """
        namespace Demo.Geometry;

        public sealed class Square
        {
            public double Side { get; init; } = 2;
        }
        """;

    [Fact]
    public async Task OneCommitCarriesEveryFileOfOneCheckpoint()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox, 5);

        var result = await BuildAsync(sandbox, IndexWriter.CheckpointFiles, TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Added);
        Assert.Equal(5, CountRows(sandbox, "SELECT id FROM files"));

        // The five files are one checkpoint, then the edges are one commit and `meta` is another.
        Assert.Equal(3, result.Commits);
    }

    [Fact]
    public async Task ACheckpointOfTwoFilesCostsOneCommitForEachPair()
    {
        using var split = ToolTestWorkspace.Create();
        WriteCorpus(split, 5);
        var perTwo = await BuildAsync(split, checkpointFiles: 2, TestContext.Current.CancellationToken);

        using var whole = ToolTestWorkspace.Create();
        WriteCorpus(whole, 5);
        var perCheckpoint = await BuildAsync(whole, IndexWriter.CheckpointFiles, TestContext.Current.CancellationToken);

        // Three checkpoints for five files, plus the edges and `meta`; the default carries all five at once.
        Assert.Equal(5, perTwo.Commits);
        Assert.Equal(3, perCheckpoint.Commits);

        // A commit boundary is not an accounting boundary: the counters and the rows are the same either way (D8).
        Assert.Equal(perCheckpoint.Added, perTwo.Added);
        Assert.Equal(0, perTwo.Changed);
        Assert.Equal(5, CountRows(split, "SELECT id FROM files"));
        Assert.Equal(
            CountRows(whole, "SELECT id FROM chunks"),
            CountRows(split, "SELECT id FROM chunks"));
    }


    [Fact]
    public async Task AnAbandonedCheckpointLeavesTheStoreAsTheLastCommitLeftIt()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", CircleSource);
        sandbox.Write("src/Square.cs", SquareSource);

        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: true);
        var database = lease.Database.Database;
        IndexSchema.EnsureCreated(database, paths);

        var scanned = Scan(sandbox);
        var writer = IndexFileWriter.For(database, extractor: new Refuses("src/Square.cs"));
        var indexedUtc = IndexTimestamps.Format(DateTimeOffset.UtcNow);
        var circle = scanned["src/Circle.cs"];
        var square = scanned["src/Square.cs"];
        var circleContent = await IndexFileContent.ReadAsync(circle.FullPath, cancellationToken);
        var squareContent = await IndexFileContent.ReadAsync(square.FullPath, cancellationToken);

        writer.BeginCheckpoint();
        writer.Write(circle, circleContent, null, indexedUtc, cancellationToken);
        Assert.Throws<InvalidOperationException>(
            () => writer.Write(square, squareContent, null, indexedUtc, cancellationToken));

        writer.AbandonCheckpoint();

        Assert.Equal(0, writer.Commits);
        Assert.Empty(IndexSql.Query(database, "SELECT id FROM files"));
        Assert.Empty(IndexSql.Query(database, "SELECT id FROM chunks"));
    }

    [Fact]
    public async Task ACheckpointCommittedBeforeTheFailureStays()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", CircleSource);
        sandbox.Write("src/Square.cs", SquareSource);

        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: true);
        var database = lease.Database.Database;
        IndexSchema.EnsureCreated(database, paths);

        var scanned = Scan(sandbox);
        var writer = IndexFileWriter.For(database, extractor: new Refuses("src/Square.cs"));
        var indexedUtc = IndexTimestamps.Format(DateTimeOffset.UtcNow);
        var circle = scanned["src/Circle.cs"];
        var square = scanned["src/Square.cs"];
        var circleContent = await IndexFileContent.ReadAsync(circle.FullPath, cancellationToken);
        var squareContent = await IndexFileContent.ReadAsync(square.FullPath, cancellationToken);

        writer.BeginCheckpoint();
        writer.Write(circle, circleContent, null, indexedUtc, cancellationToken);
        writer.EndCheckpoint();

        writer.BeginCheckpoint();
        Assert.Throws<InvalidOperationException>(
            () => writer.Write(square, squareContent, null, indexedUtc, cancellationToken));
        writer.AbandonCheckpoint();

        Assert.Equal(1, writer.Commits);
        Assert.Single(IndexSql.Query(database, "SELECT id FROM files"));
    }

    [Fact]
    public async Task AZeroBudgetCommitsNothingButMeta()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox, 5);

        var result = await BuildAsync(
            sandbox,
            IndexWriter.CheckpointFiles,
            TestContext.Current.CancellationToken,
            TimeSpan.Zero,
            IndexBudgetClock.ProcessorTime);

        Assert.True(result.Incomplete);
        Assert.Equal(0, result.Added);

        // The checkpoint never opened a batch, so the only commit a build that wrote nothing pays is `meta`'s (D2).
        Assert.Equal(1, result.Commits);
        Assert.Equal(0, CountRows(sandbox, "SELECT id FROM files"));
    }

    [Fact]
    public async Task ABudgetThatExpiresInsideACheckpointKeepsTheGroup()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox, 200);

        var result = await BuildAsync(
            sandbox,
            IndexWriter.CheckpointFiles,
            TestContext.Current.CancellationToken,

            // Generous against the scan and small against two hundred files: the walk of an empty workspace costs tens
            // of milliseconds even on a loaded machine, and the corpus costs seconds, so the stop lands inside the
            // checkpoint rather than before it.
            TimeSpan.FromMilliseconds(800),
            IndexBudgetClock.WallClock);

        Assert.True(result.Incomplete);
        Assert.True(result.Added >= 1, "the first file should have been written before the budget expired");
        Assert.True(result.Added < 200, "the budget should have stopped the walk before its last file");

        // What the loop held when the budget ran out was committed before it broke: the rows exist and are the ones the
        // counters name. Then the edges and `meta`, which a partial build still writes — and one commit per checkpoint
        // for a run that was cut in half is still a commit per checkpoint rather than a commit per file.
        var perCheckpoint = (result.Added + IndexWriter.CheckpointFiles - 1) / IndexWriter.CheckpointFiles;

        Assert.Equal(result.Added, CountRows(sandbox, "SELECT id FROM files"));
        Assert.Equal(perCheckpoint + 2, result.Commits);
        Assert.True(result.Commits < result.Added, "a partial build must not have committed once per file");
    }

    [Fact]
    public async Task ACancelledBuildWritesNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox, 5);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BuildAsync(sandbox, IndexWriter.CheckpointFiles, cancellationToken: cancellation.Token));

        Assert.Equal(0, CountRows(sandbox, "SELECT id FROM files"));
    }

    [Fact]
    public async Task APruneRemovesItsWholeSetInOneCommit()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox, 5);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.RebuildAsync(IndexBudgetClock.ProcessorTime, TestContext.Current.CancellationToken);

        foreach (var name in new[] { "Unit00.cs", "Unit01.cs", "Unit02.cs" })
        {
            File.Delete(Path.Combine(sandbox.Root, "src", name));
        }

        var report = await maintainer.PruneAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Removed);
        Assert.Equal(1, report.Commits);
        Assert.Equal(2, CountRows(sandbox, "SELECT id FROM files"));
    }

    [Fact]
    public async Task TwoOwnersOfOneBatchAreRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", CircleSource);

        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: true);
        IndexSchema.EnsureCreated(lease.Database.Database, paths);

        var writer = IndexFileWriter.For(lease.Database.Database);

        Assert.Throws<InvalidOperationException>(writer.EndCheckpoint);

        writer.BeginCheckpoint();

        Assert.Throws<InvalidOperationException>(writer.BeginCheckpoint);

        writer.AbandonCheckpoint();

        Assert.Equal(0, writer.Commits);
    }


    private static async Task<IndexWriteResult> BuildAsync(
        ToolTestWorkspace sandbox,
        int checkpointFiles,
        CancellationToken cancellationToken,
        TimeSpan? budget = null,
        IndexBudgetClock clock = IndexBudgetClock.ProcessorTime)
    {
        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: true);
        var database = lease.Database.Database;
        IndexSchema.EnsureCreated(database, paths);

        return await IndexWriter
            .For(sandbox.Workspace, database, checkpointFiles)
            .WriteAsync(budget ?? TimeSpan.FromMinutes(1), clock, IndexBuildId.NewId(), cancellationToken);
    }

    private static Dictionary<string, ScannedFile> Scan(ToolTestWorkspace sandbox)
    {
        var scanned = new Dictionary<string, ScannedFile>(StringComparer.Ordinal);
        foreach (var file in WorkspaceFileScanner.For(sandbox.Workspace).Scan(TestContext.Current.CancellationToken))
        {
            scanned[file.Path] = file;
        }

        return scanned;
    }

    private static int CountRows(ToolTestWorkspace sandbox, string sql)
    {
        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        return IndexSql.Query(lease.Database.Database, sql).Count;
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox, int files)
    {
        for (var index = 0; index < files; index++)
        {
            var name = index.ToString("D2", CultureInfo.InvariantCulture);
            var previous = ((index + files - 1) % files).ToString("D2", CultureInfo.InvariantCulture);

            // Every file calls the one before it, so the corpus has relations for the edges phase to resolve and that
            // phase has a commit of its own. Five unrelated classes would cost one commit fewer, and the arithmetic
            // these tests assert is the arithmetic of a build that writes edges.
            var source = $$"""
                namespace Demo.Units;

                public sealed class Unit{{name}}
                {
                    public int Value() => {{index}};

                    public int Measured()
                    {
                        var previous = new Unit{{previous}}();
                        return previous.Value();
                    }
                }
                """;

            sandbox.Write("src/Unit" + name + ".cs", source);
        }
    }

    /// <summary>An extractor that refuses one path, so a checkpoint can fail the way a bad file makes one fail.</summary>
    private sealed class Refuses(string path) : ISyntaxFactsExtractor
    {
        public string Id => "refusing/1";

        public SyntaxFacts Extract(ScannedFile file, string text, CancellationToken cancellationToken) =>
            string.Equals(file.Path, path, StringComparison.Ordinal)
                ? throw new InvalidOperationException("this file is the one that fails")
                : HeuristicSyntaxFactsExtractor.Create().Extract(file, text, cancellationToken);
    }
}

