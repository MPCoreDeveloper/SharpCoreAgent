using System.Diagnostics;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// Adds up what each step of one build cost, on both clocks.
/// </summary>
/// <remarks>
/// One instance belongs to one build, for the same reason <see cref="IndexWriter"/> does: a step that throws must not
/// leave a total behind that the next build adds to. Steps are timed by wrapping the work rather than by opening and
/// closing a scope by hand, so a step cannot be left open by a changed control flow.
/// <para>
/// Every step of <see cref="IndexPhases.Order"/> is present from the start with zeros, which is what makes a report
/// addable: a step that did nothing says so instead of being absent. A name that is not in the order is refused,
/// because a typo would otherwise cost a step's time without anyone noticing it was gone.
/// </para>
/// </remarks>
internal sealed class IndexPhaseClock
{
    private readonly Dictionary<string, (TimeSpan Wall, TimeSpan Processor)> _totals = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a clock with an entry for every step of a build.
    /// </summary>
    internal IndexPhaseClock()
    {
        foreach (var name in IndexPhases.Order)
        {
            _totals[name] = (TimeSpan.Zero, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Times one unit of work.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="phase">The step the work belongs to.</param>
    /// <param name="work">The work.</param>
    /// <returns>Whatever <paramref name="work"/> returned.</returns>
    internal T Measure<T>(string phase, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var started = Begin(phase);
        try
        {
            return work();
        }
        finally
        {
            End(phase, started);
        }
    }

    /// <summary>
    /// Times one unit of work that returns nothing.
    /// </summary>
    /// <param name="phase">The step the work belongs to.</param>
    /// <param name="work">The work.</param>
    internal void Measure(string phase, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var started = Begin(phase);
        try
        {
            work();
        }
        finally
        {
            End(phase, started);
        }
    }

    /// <summary>
    /// Times one unit of work that completes asynchronously.
    /// </summary>
    /// <param name="phase">The step the work belongs to.</param>
    /// <param name="work">The work.</param>
    /// <returns>A task that completes when the work does.</returns>
    /// <remarks>
    /// The whole await is timed, including whatever the work yields to, because a step that waits on the thread pool
    /// still costs the caller the clock it spends waiting — and that is the number a developer compares against the
    /// processor time beside it.
    /// </remarks>
    internal async Task MeasureAsync(string phase, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var started = Begin(phase);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            End(phase, started);
        }
    }

    /// <summary>
    /// Times one unit of work that completes asynchronously and produces a value.
    /// </summary>
    /// <typeparam name="T">What the work produces.</typeparam>
    /// <param name="phase">The step the work belongs to.</param>
    /// <param name="work">The work.</param>
    /// <returns>A task that completes with whatever <paramref name="work"/> produced.</returns>
    /// <remarks>
    /// This overload and the <see cref="Task{TResult}"/> one below exist for the same reason: the build's own awaited
    /// steps return a <see cref="ValueTask{TResult}"/> and the store's algorithms return a <see cref="Task{TResult}"/>,
    /// and a caller that had to wrap one into the other just to be timed would be paying for the measurement.
    /// </remarks>
    internal async Task<T> MeasureAsync<T>(string phase, Func<ValueTask<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var started = Begin(phase);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            End(phase, started);
        }
    }

    /// <summary>
    /// Times one unit of work that completes asynchronously and produces a value.
    /// </summary>
    /// <typeparam name="T">What the work produces.</typeparam>
    /// <param name="phase">The step the work belongs to.</param>
    /// <param name="work">The work.</param>
    /// <returns>A task that completes with whatever <paramref name="work"/> produced.</returns>
    internal async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var started = Begin(phase);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            End(phase, started);
        }
    }

    /// <summary>
    /// Gets what each step cost so far, in build order.
    /// </summary>
    /// <returns>One entry per step, zeros included.</returns>
    internal IReadOnlyList<IndexPhase> Elapsed() =>
    [
        .. IndexPhases.Order.Select(name => new IndexPhase
        {
            Name = name,
            Wall = _totals[name].Wall,
            Processor = _totals[name].Processor,
            Nested = IndexPhases.IsNested(name),
        }),
    ];

    /// <summary>
    /// Starts timing one step.
    /// </summary>
    /// <param name="phase">The step's name.</param>
    /// <returns>The wall clock and the processor time at the start of the step.</returns>
    /// <exception cref="ArgumentException">The name is not one of <see cref="IndexPhases.Order"/>.</exception>
    private (long Wall, TimeSpan Processor) Begin(string phase)
    {
        if (!_totals.ContainsKey(phase))
        {
            throw new ArgumentException($"'{phase}' is not a phase of a build.", nameof(phase));
        }

        return (Stopwatch.GetTimestamp(), Environment.CpuUsage.TotalTime);
    }

    /// <summary>
    /// Adds one finished step to its total.
    /// </summary>
    /// <param name="phase">The step's name.</param>
    /// <param name="started">The values <see cref="Begin"/> returned.</param>
    private void End(string phase, (long Wall, TimeSpan Processor) started)
    {
        var wall = Stopwatch.GetElapsedTime(started.Wall);
        var processor = Environment.CpuUsage.TotalTime - started.Processor;
        var total = _totals[phase];

        _totals[phase] = (total.Wall + wall, total.Processor + processor);
    }
}
