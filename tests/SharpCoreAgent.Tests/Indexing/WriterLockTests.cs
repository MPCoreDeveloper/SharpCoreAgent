using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// One writer, many readers: the lock that keeps two SharpCoreAgent processes from writing the same index at once,
/// and the build id that lets a reader tell one build from the next.
/// </summary>
/// <remarks>
/// Exclusivity is tested against real file handles rather than a mock, because the property being bought is the
/// operating system's: a <c>FileShare.None</c> handle is what makes a second process see a lock instead of a
/// half-written database.
/// </remarks>
public sealed class WriterLockTests
{
    [Fact]
    public async Task SecondWriterCannotTakeAHeldLock()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);
        paths.EnsureDirectoryCreated();

        // A raw handle on the lock file is what another process looks like from here: the file is open without
        // shared access, and this process knows nothing about the handle that owns it.
        using (var other = new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(IndexWriterLock.IsHeldByAnotherWriter(paths));

            var blocked = await IndexWriterLock.TryAcquireAsync(
                paths,
                TimeSpan.Zero,
                TestContext.Current.CancellationToken);

            Assert.Null(blocked);
        }

        Assert.False(IndexWriterLock.IsHeldByAnotherWriter(paths));

        using var acquired = await IndexWriterLock.TryAcquireAsync(
            paths,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.NotNull(acquired);

        // The lock this process holds is not "another writer": the question the probe answers is "may I write?".
        Assert.False(IndexWriterLock.IsHeldByAnotherWriter(paths));
    }

    [Fact]
    public async Task StaleLockFileDoesNotDeadlockTheNextRun()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);
        paths.EnsureDirectoryCreated();

        // What a process killed mid-build leaves behind: the file, and nobody holding it.
        await File.WriteAllTextAsync(paths.LockPath, string.Empty, TestContext.Current.CancellationToken);

        using var writer = await IndexWriterLock.TryAcquireAsync(
            paths,
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        Assert.NotNull(writer);
    }

    [Fact]
    public async Task MaintainerReportsBuildingWhenAnotherWriterHoldsTheLock()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);
        paths.EnsureDirectoryCreated();

        using var held = new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);

        var report = await maintainer.EnsureFreshAsync(
            TimeSpan.FromMilliseconds(150),
            TestContext.Current.CancellationToken);

        // Losing the race is not an error: the answer is "somebody else is on it", which the caller prints and
        // the next turn retries.
        Assert.Equal(IndexFreshness.Building, report.Status.Freshness);
        Assert.True(report.Incomplete);
        Assert.Contains(SharpCoreDbIndexMaintainer.BuildingNote, report.Notes);
        Assert.False(maintainer.HoldsWriterLock);
        Assert.Null(maintainer.BuildId);
    }

    [Fact]
    public async Task ReleasedLockLetsTheNextRunBuild()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);

        string? firstBuild;
        string? secondBuild;

        await using (var first = SharpCoreDbIndexMaintainer.For(sandbox.Workspace))
        {
            var report = await first.EnsureFreshAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.NotEqual(IndexFreshness.Building, report.Status.Freshness);
            firstBuild = first.BuildId;
            Assert.NotNull(firstBuild);
        }

        // The lock is gone once the run is over, so a second maintainer in the same process can take it, and it
        // spends a new id: one ULID per run, never one per instance.
        Assert.False(IndexWriterLock.IsHeldByAnotherWriter(paths));

        await using (var second = SharpCoreDbIndexMaintainer.For(sandbox.Workspace))
        {
            var report = await second.EnsureFreshAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.NotEqual(IndexFreshness.Building, report.Status.Freshness);
            secondBuild = second.BuildId;
        }

        Assert.NotNull(secondBuild);
        Assert.NotEqual(firstBuild, secondBuild);
    }
}
