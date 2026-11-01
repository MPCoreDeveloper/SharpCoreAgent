using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Infrastructure.McpServer;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The refresh a call pays for: once per interval, inside a ceiling, and never at the cost of the answer.
/// </summary>
/// <remarks>
/// The clock is the test's, not the wall's, because the interval is five seconds and a suite that slept through it
/// would spend the time of a build proving nothing. What the gate does with the maintainer — open, budget, release —
/// is asserted here; that the index itself refreshes is <c>RetrievalToolIndexTests</c>' subject.
/// </remarks>
public sealed class IndexRefreshGateTests
{
    [Fact]
    public async Task TheFirstCallRefreshesWithTheConfiguredBudget()
    {
        var clock = new Clock();
        var (gate, opened, log) = Build(clock: clock, budget: TimeSpan.FromSeconds(1.5));

        await gate.PrepareAsync(Token);

        var maintainer = Assert.Single(opened);
        Assert.Equal([TimeSpan.FromSeconds(1.5)], maintainer.Budgets);
        Assert.True(maintainer.Released);
        Assert.Contains(log, line => line.StartsWith("refresh: missing, 2 changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondCallInsideTheIntervalDoesNotWalkTheTree()
    {
        var clock = new Clock();
        var (gate, opened, log) = Build(clock: clock);

        await gate.PrepareAsync(Token);
        clock.Advance(TimeSpan.FromSeconds(2));
        await gate.PrepareAsync(Token);

        Assert.Single(opened);
        Assert.Contains(log, line => line.StartsWith("refresh: skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACallAfterTheIntervalRefreshesAgain()
    {
        var clock = new Clock();
        var (gate, opened, _) = Build(clock: clock);

        await gate.PrepareAsync(Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        await gate.PrepareAsync(Token);

        Assert.Equal(2, opened.Count);
        Assert.All(opened, maintainer => Assert.True(maintainer.Released));
    }

    [Fact]
    public async Task NoRefreshOpensNoMaintainerAtAll()
    {
        var (gate, opened, log) = Build(refresh: false);

        await gate.PrepareAsync(Token);
        await gate.PrepareAsync(Token);

        Assert.Empty(opened);
        Assert.Empty(log);
    }

    [Fact]
    public async Task ARefreshThatCannotRunIsNotAnError()
    {
        var (gate, opened, log) = Build(work: () => throw new IOException("the database is locked"));

        // Neither throws nor answers anything: the tool's own heading is what reports freshness, and the call
        // continues to the index that is there.
        await gate.PrepareAsync(Token);

        Assert.Single(opened);
        Assert.True(opened[0].Released);
        Assert.Contains("refresh: did not run: the database is locked", log);
    }

    [Fact]
    public async Task AFailedRefreshIsNotRetriedByEveryCallThatFollows()
    {
        // A failure costs the whole budget, so the interval is stamped before the attempt rather than after a
        // success. Otherwise one locked database turns six quick questions into six timeouts.
        var clock = new Clock();
        var (gate, opened, log) = Build(clock: clock, work: () => throw new IOException("busy"));

        await gate.PrepareAsync(Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        await gate.PrepareAsync(Token);

        Assert.Single(opened);
        Assert.Contains(log, line => line.StartsWith("refresh: skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefreshReleaseHappensEvenWhenTheWorkThrowsSynchronously()
    {
        var (gate, opened, _) = Build(work: () => throw new InvalidOperationException("corrupt"));

        await gate.PrepareAsync(Token);

        Assert.True(Assert.Single(opened).Released);
    }

    [Fact]
    public async Task CancellationTravelsAndTheHandleIsStillReleased()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (gate, opened, _) = Build(work: () => throw new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.PrepareAsync(cancelled.Token).AsTask());

        // The release is in a finally, so the one thing a cancelled call may not do is leave the file open.
        Assert.True(Assert.Single(opened).Released);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IndexBuildReport Report() => new()
    {
        Status = IndexStatus.Missing("C:\\work"),
        Added = 1,
        Changed = 1,
        Unchanged = 3,
    };

    private static (IndexRefreshGate Gate, List<FakeMaintainer> Opened, List<string> Log) Build(
        Func<IndexBuildReport>? work = null,
        bool refresh = true,
        TimeSpan? budget = null,
        TimeSpan? interval = null,
        Clock? clock = null)
    {
        var opened = new List<FakeMaintainer>();
        var log = new List<string>();
        var options = new McpServerOptions
        {
            Refresh = refresh,
            RefreshBudget = budget ?? TimeSpan.FromSeconds(3),
            RefreshInterval = interval ?? TimeSpan.FromSeconds(5),
        };

        var gate = new IndexRefreshGate(
            () =>
            {
                var maintainer = new FakeMaintainer(() => new ValueTask<IndexBuildReport>((work ?? Report)()));
                opened.Add(maintainer);
                return maintainer;
            },
            options,
            log.Add,
            clock);

        return (gate, opened, log);
    }

    /// <summary>A clock the test moves, so the five-second interval costs no wall-clock time.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The write port, recording the budget it was given and whether it was released.</summary>
    private sealed class FakeMaintainer(Func<ValueTask<IndexBuildReport>> work) : ICodeIndexMaintainer, IAsyncDisposable
    {
        public List<TimeSpan> Budgets { get; } = [];

        public bool Released { get; private set; }

        public ValueTask<IndexBuildReport> EnsureFreshAsync(TimeSpan budget, CancellationToken cancellationToken)
        {
            Budgets.Add(budget);
            return work();
        }

        public ValueTask<IndexBuildReport> EnsureFreshAsync(
            TimeSpan budget,
            IndexBudgetClock clock,
            CancellationToken cancellationToken) => EnsureFreshAsync(budget, cancellationToken);

        public ValueTask<IndexBuildReport> RebuildAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("the gate never rebuilds an index");

        public ValueTask<IndexBuildReport> RebuildAsync(IndexBudgetClock clock, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the gate never rebuilds an index");

        public ValueTask<IndexVerification> VerifyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("the gate never verifies an index");

        public ValueTask<IndexBuildReport> PruneAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("the gate never prunes an index");

        public ValueTask DisposeAsync()
        {
            Released = true;
            return ValueTask.CompletedTask;
        }
    }
}
