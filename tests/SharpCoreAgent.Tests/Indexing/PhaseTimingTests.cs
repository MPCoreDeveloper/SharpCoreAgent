using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// What a real build reports about its own steps: every step present and in order, the steps adding up to no more
/// than the run they belong to, and the store's commit staying inside the steps that contain it.
/// </summary>
/// <remarks>
/// These read the report the command line reads, so they also cover the wiring — a phase measured in the writer but
/// dropped on the way into <see cref="IndexBuildReport"/> would fail here rather than only in a terminal.
/// </remarks>
public sealed class PhaseTimingTests
{
    [Fact]
    public async Task ABuildReportsEveryPhaseInBuildOrder()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.RebuildAsync(
            IndexBudgetClock.ProcessorTime,
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexPhases.Order, report.Phases.Select(static phase => phase.Name).ToArray());
        Assert.Equal(2, report.Added);
    }

    [Fact]
    public async Task TheWritingAndReadingPhasesCostSomethingAndTheRestNeedNot()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.RebuildAsync(
            IndexBudgetClock.ProcessorTime,
            TestContext.Current.CancellationToken);

        var phases = report.Phases.ToDictionary(static phase => phase.Name, StringComparer.Ordinal);

        Assert.True(phases[IndexPhases.Scan].Wall > TimeSpan.Zero);
        Assert.True(phases[IndexPhases.Load].Wall > TimeSpan.Zero);
        Assert.True(phases[IndexPhases.Write].Wall > TimeSpan.Zero);
    }

    [Fact]
    public async Task ThePhasesAccountForNoMoreTimeThanTheRunTook()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.RebuildAsync(
            IndexBudgetClock.ProcessorTime,
            TestContext.Current.CancellationToken);

        var accounted = report.Phases
            .Where(static phase => !phase.Nested)
            .Aggregate(TimeSpan.Zero, static (sum, phase) => sum + phase.Wall);

        Assert.True(
            accounted <= TimeSpan.FromMilliseconds(report.ElapsedMilliseconds) + TimeSpan.FromMilliseconds(100),
            $"the phases add up to {accounted} but the run reports {report.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TheStoreCommitStaysInsideThePhasesThatContainIt()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.RebuildAsync(
            IndexBudgetClock.ProcessorTime,
            TestContext.Current.CancellationToken);

        var nested = report.Phases
            .Where(static phase => phase.Nested)
            .Aggregate(TimeSpan.Zero, static (sum, phase) => sum + phase.Wall);

        var containing = report.Phases
            .Where(static phase => phase.Name is IndexPhases.Write or IndexPhases.Remove or IndexPhases.Edges or IndexPhases.Meta)
            .Aggregate(TimeSpan.Zero, static (sum, phase) => sum + phase.Wall);

        Assert.True(
            nested <= containing + TimeSpan.FromMilliseconds(100),
            $"the commit took {nested} but the phases containing it took {containing}");
    }

    [Fact]
    public async Task APruneIsNotABuildSoItReportsNoPhases()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        await maintainer.RebuildAsync(IndexBudgetClock.ProcessorTime, TestContext.Current.CancellationToken);

        File.Delete(Path.Combine(sandbox.Root, "src", "Circle.cs"));

        var report = await maintainer.PruneAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Removed);
        Assert.Empty(report.Phases);
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write(
            "src/Circle.cs",
            """
            namespace Demo.Geometry;

            internal sealed class Circle
            {
                internal double Circumference() => 6.28;
            }
            """);

        sandbox.Write(
            "src/Square.cs",
            """
            namespace Demo.Geometry;

            internal sealed class Square
            {
                internal double Side { get; init; } = 2;
            }
            """);
    }
}
