using System.Diagnostics;
using SharpCoreAgent.Indexing;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The clock a build adds its steps up on: what it reports before anything ran, how repeated entries of one step
/// combine, and which names it refuses.
/// </summary>
/// <remarks>
/// The assertions here are about arithmetic and ordering rather than about durations, except where a test burns a
/// known amount of time to prove that two entries of the same step add up instead of replacing one another.
/// </remarks>
public sealed class IndexPhaseClockTests
{
    [Fact]
    public void AnUntouchedClockReportsEveryPhaseAsZeroInBuildOrder()
    {
        var phases = new IndexPhaseClock().Elapsed();

        Assert.Equal(IndexPhases.Order, phases.Select(static phase => phase.Name).ToArray());
        Assert.All(phases, static phase =>
        {
            Assert.Equal(TimeSpan.Zero, phase.Wall);
            Assert.Equal(TimeSpan.Zero, phase.Processor);
        });
    }

    [Fact]
    public void OnlyTheStepsThatRunInsideAnotherStepAreNested()
    {
        var phases = new IndexPhaseClock().Elapsed();

        Assert.Equal(
            [
                IndexPhases.Nodes,
                IndexPhases.Adjacency,
                IndexPhases.Louvain,
                IndexPhases.Degree,
                IndexPhases.Assign,
                IndexPhases.Commit,
            ],
            phases.Where(static phase => phase.Nested).Select(static phase => phase.Name).ToArray());
    }

    [Fact]
    public void APhaseFollowsTheStepItExplains()
    {
        var order = IndexPhases.Order.ToList();

        // The clusterer's steps follow `communities` and stop before `meta`, or the block would read as if they were
        // its siblings.
        Assert.Equal(
            [IndexPhases.Nodes, IndexPhases.Adjacency, IndexPhases.Louvain, IndexPhases.Degree, IndexPhases.Assign],
            order
                .SkipWhile(static name => name != IndexPhases.Communities)
                .Skip(1)
                .TakeWhile(static name => name != IndexPhases.Meta)
                .ToArray());
    }

    [Fact]
    public void RepeatedEntriesOfOnePhaseAddUpInsteadOfReplacing()
    {
        var clock = new IndexPhaseClock();

        clock.Measure(IndexPhases.Commit, () => Burn(TimeSpan.FromMilliseconds(25)));
        clock.Measure(IndexPhases.Commit, () => Burn(TimeSpan.FromMilliseconds(25)));
        clock.Measure(IndexPhases.Write, () => Burn(TimeSpan.FromMilliseconds(25)));

        var phases = clock.Elapsed().ToDictionary(static phase => phase.Name, StringComparer.Ordinal);

        Assert.True(phases[IndexPhases.Commit].Wall > phases[IndexPhases.Write].Wall);
        Assert.True(phases[IndexPhases.Commit].Wall >= TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task AnAwaitedPhaseIsTimedAcrossItsAwait()
    {
        var clock = new IndexPhaseClock();

        await clock.MeasureAsync(
            IndexPhases.Communities,
            async () => await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken));

        var phase = clock.Elapsed().Single(static candidate => candidate.Name == IndexPhases.Communities);

        Assert.True(phase.Wall >= TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task APHaseThatProducesAValueIsTimedAndHandsItBack()
    {
        var clock = new IndexPhaseClock();

        // Both overloads, because the store's algorithms return a Task<T> and the build's own awaited steps return a
        // ValueTask<T>: a step that had to be wrapped into the other shape to be timed would be paying for the clock.
        Func<Task<int>> asTask = async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
            return 41;
        };

        Func<ValueTask<int>> asValueTask = async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
            return 42;
        };

        Assert.Equal(41, await clock.MeasureAsync(IndexPhases.Degree, asTask));
        Assert.Equal(42, await clock.MeasureAsync(IndexPhases.Louvain, asValueTask));

        var phases = clock.Elapsed().ToDictionary(static phase => phase.Name, StringComparer.Ordinal);

        Assert.True(phases[IndexPhases.Degree].Wall >= TimeSpan.FromMilliseconds(25));
        Assert.True(phases[IndexPhases.Louvain].Wall >= TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public void ANameThatIsNotAPhaseIsRefused()
    {
        var clock = new IndexPhaseClock();

        Assert.Throws<ArgumentException>(() => clock.Measure("scanning", static () => 1));
    }

    private static void Burn(TimeSpan duration)
    {
        var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var swirled = 0d;

        while (Stopwatch.GetTimestamp() < until)
        {
            swirled += Math.Sqrt(swirled + 1);
        }

        GC.KeepAlive(swirled);
    }
}
