using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// <c>index --compact</c>: the pages a rewritten index file stops using are given back, and every row that is still
/// live survives the rewrite.
/// </summary>
/// <remarks>
/// The dead space is produced here rather than found: rows are inserted through the store and then deleted, which frees
/// each row and no page. It is the same shape the product produces for itself — every writing run drops and rewrites
/// passages, and a rebuild empties every table before filling it — in a size a test can assert (spec 030).
/// </remarks>
public sealed class CompactionTests
{
    private const int BloatRows = 40;

    private const int BloatRowBytes = 256 * 1024;

    [Fact]
    public async Task CompactionGivesBackThePagesDeletedRowsUsed()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var built = await maintainer.RebuildAsync(TestContext.Current.CancellationToken);
        var paths = IndexPaths.For(sandbox.Workspace);

        EnsureDeadSpace(paths, BloatRows);
        var bloated = new FileInfo(paths.DatabasePath).Length;
        Assert.True(bloated > 0, "the index file should exist after a rebuild");

        var compaction = await maintainer.CompactAsync(TestContext.Current.CancellationToken);

        Assert.True(compaction.Succeeded, compaction.Note);
        Assert.True(
            compaction.ReclaimedBytes > 0,
            $"the file was {compaction.BeforeBytes} bytes before the rewrite and {compaction.AfterBytes} after it");
        Assert.Equal(new FileInfo(paths.DatabasePath).Length, compaction.AfterBytes);
        Assert.True(compaction.AfterBytes < compaction.BeforeBytes);

        // The numbers the rewrite is not allowed to change: the passages, and the file row that is still there.
        Assert.Equal(built.Status.Chunks, compaction.Status.Chunks);
        Assert.Equal(1, compaction.Status.Files);
        Assert.True((await maintainer.VerifyAsync(TestContext.Current.CancellationToken)).IsHealthy);
    }

    [Fact]
    public async Task SecondCompactionFindsNothingLeftToGiveBack()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.RebuildAsync(TestContext.Current.CancellationToken);

        EnsureDeadSpace(IndexPaths.For(sandbox.Workspace), BloatRows);

        var first = await maintainer.CompactAsync(TestContext.Current.CancellationToken);
        var second = await maintainer.CompactAsync(TestContext.Current.CancellationToken);

        Assert.True(first.ReclaimedBytes > 0, "the first rewrite had dead space to reclaim");
        Assert.True(second.Succeeded, second.Note);
        Assert.Equal(0, second.ReclaimedBytes);
        Assert.Equal(first.AfterBytes, second.AfterBytes);
    }

    [Fact]
    public async Task CompactingAWorkspaceNobodyIndexedCreatesNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", "internal sealed class Circle { }");

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var compaction = await maintainer.CompactAsync(TestContext.Current.CancellationToken);

        Assert.True(compaction.Succeeded);
        Assert.Equal(0, compaction.ReclaimedBytes);
        Assert.Equal("there is no index to compact", compaction.Note);
        Assert.False(File.Exists(IndexPaths.For(sandbox.Workspace).DatabasePath));
    }

    [Fact]
    public async Task ARebuildCompactsWhatItLeftBehindAndSaysSo()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        // A file the scanner skips for its size, so the report has a note of its own: the compaction's line has to be
        // readable ahead of it rather than counted among the ten a report prints (spec 030, D5).
        sandbox.Write("src/Huge.cs", new string('x', 2 * 1024 * 1024));

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.RebuildAsync(TestContext.Current.CancellationToken);

        var paths = IndexPaths.For(sandbox.Workspace);
        EnsureDeadSpace(paths, BloatRows, delete: false);
        var bloated = new FileInfo(paths.DatabasePath).Length;

        var rebuilt = await maintainer.RebuildAsync(TestContext.Current.CancellationToken);

        Assert.True(rebuilt.Notes.Count >= 2, "the skipped file should have produced a note of its own");
        Assert.Contains("compacted", rebuilt.Notes[0], StringComparison.Ordinal);

        // Two rows: the corpus file, and the skipped one — which is a row the build records even though it never reads
        // the file, and is what makes the note above exist.
        Assert.Equal(2, rebuilt.Status.Files);
        Assert.True(
            new FileInfo(paths.DatabasePath).Length < bloated,
            "a rebuild leaves the file smaller than it found it, because it compacts what it wrote");
        Assert.True((await maintainer.VerifyAsync(TestContext.Current.CancellationToken)).IsHealthy);
    }

    /// <summary>
    /// Writes rows the corpus does not have, and drops them again unless asked to keep them.
    /// </summary>
    /// <param name="paths">The index to grow.</param>
    /// <param name="rows">How many rows to write.</param>
    /// <param name="delete">Whether to drop them, which frees the rows and not the pages.</param>
    /// <remarks>
    /// A few rows of a quarter of a megabyte each rather than many small ones: what has to grow is the file, and a small
    /// index holds megabytes of slack that small rows reuse instead of extending (spec 030). Forty rows are also cheap
    /// to write, which keeps this test a second rather than a minute.
    /// </remarks>
    private static void EnsureDeadSpace(IndexPaths paths, int rows, bool delete = true)
    {
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        var database = lease.Database.Database;

        for (var row = 0; row < rows; row++)
        {
            IndexSql.Insert(
                database,
                IndexTable.Chunks,
                IndexSql.Row(
                    ("file_id", 1L),
                    ("start_line", 900 + row),
                    ("end_line", 901 + row),
                    ("kind", "chunk"),
                    ("text", new string('x', BloatRowBytes)),
                    ("tokens", "x"),
                    ("sha256", new string('c', 64))));
        }

        if (delete)
        {
            database.ExecuteSQL("DELETE FROM chunks WHERE start_line >= 900");
        }
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox) =>
        sandbox.Write(
            "src/Circle.cs",
            """
            namespace Demo.Geometry;

            internal sealed class Circle
            {
                internal double Circumference() => 6.28;
            }
            """);
}
