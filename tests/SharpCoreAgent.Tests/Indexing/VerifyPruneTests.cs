using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// <c>index --verify</c> and <c>index --prune</c>: the two commands a caller reaches for when the incremental
/// build's shortcuts are no longer trusted.
/// </summary>
/// <remarks>
/// The cases are the four ways an index and a directory tree disagree, each produced by hand rather than by a
/// failed build: a deleted file, a file the index never saw, a passage whose stored text no longer matches its
/// hash, and a row that points at a file row nobody has.
/// </remarks>
public sealed class VerifyPruneTests
{
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

    [Fact]
    public async Task CleanIndexIsHealthy()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var verification = await maintainer.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.True(verification.IsHealthy, string.Join(" / ", verification.Details));
        Assert.Equal(0, verification.FilesMissingFromIndex);
        Assert.Equal(0, verification.FilesMissingOnDisk);
        Assert.Equal(0, verification.HashMismatches);
        Assert.Equal(0, verification.Orphans);
    }

    [Fact]
    public async Task DeletedFileIsReportedAndPruned()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        File.Delete(sandbox.FullPath("src/Geometry/Circle.cs"));

        var verification = await maintainer.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.False(verification.IsHealthy);
        Assert.Equal(1, verification.FilesMissingOnDisk);
        Assert.Contains(verification.Details, line => line.Contains("Circle.cs", StringComparison.Ordinal));

        var pruned = await maintainer.PruneAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, pruned.Removed);
        Assert.Equal(1, pruned.Status.Files);
        Assert.True((await maintainer.VerifyAsync(TestContext.Current.CancellationToken)).IsHealthy);
    }

    [Fact]
    public async Task FileOnDiskThatWasNeverIndexedIsReported()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // Written straight to disk, so the index has no row for it: exactly what a verification is for.
        sandbox.Write("src/Late.cs", "namespace Demo; public static class Late { }");

        var verification = await maintainer.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, verification.FilesMissingFromIndex);
        Assert.Contains(verification.Details, line => line.Contains("Late.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EditedPassageFailsItsHash()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Execute(sandbox, "UPDATE chunks SET text = 'tampered passage' WHERE id > 0");

        var verification = await maintainer.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.False(verification.IsHealthy);
        Assert.True(verification.HashMismatches >= 1);
        Assert.Contains(verification.Details, line => line.Contains("does not match its hash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingIndexIsUnhealthyNotAnException()
    {
        using var sandbox = ToolTestWorkspace.Create();

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var verification = await maintainer.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.False(verification.IsHealthy);
        Assert.Contains(verification.Details, line => line.Contains("no index", StringComparison.Ordinal));

        var pruned = await maintainer.PruneAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, pruned.Removed);
    }

    [Fact]
    public async Task ChangedFileCountsAsAHashMismatch()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        sandbox.Write("src/Geometry/Circle.cs", CircleSource + "\n// an edit\n");

        var verification = await maintainer.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, verification.HashMismatches);
        Assert.Contains(verification.Details, line => line.Contains("changed on disk", StringComparison.Ordinal));
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Shape.cs", ShapeSource);
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
    }

    private static void Execute(ToolTestWorkspace sandbox, string sql)
    {
        var paths = IndexPaths.For(sandbox.Workspace);
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        IndexSql.Execute(lease.Database.Database, sql);
    }
}
