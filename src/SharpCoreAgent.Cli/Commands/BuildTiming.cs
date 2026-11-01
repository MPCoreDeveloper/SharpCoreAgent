using System.Globalization;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Two clocks over one window: how long the work took, and how much of that the processor was busy.
/// </summary>
/// <remarks>
/// One number cannot tell a slow index from a suspended laptop, and the difference between those two cost this
/// repository half a day: a build reported thirty thousand seconds because the machine spent eight hours in Modern
/// Standby, and the report had nothing to compare it against (spec 002, D19). Printing both clocks makes the
/// difference visible, and <see cref="Divergence"/> names it when the gap is large enough that it is the story.
/// </remarks>
/// <param name="Wall">The elapsed time the work reports.</param>
/// <param name="Processor">The processor time the same window cost, process-wide.</param>
internal readonly record struct BuildTiming(TimeSpan Wall, TimeSpan Processor)
{
    /// <summary>The smallest gap between the two clocks that is worth a line of its own.</summary>
    /// <remarks>
    /// Under a minute the difference is ordinary: the thread pool warms up, a file read blocks, a connection is
    /// opened. Reporting those would train a reader to ignore the line that matters.
    /// </remarks>
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets a value indicating whether the wall clock so far exceeds the processor time that the difference is the
    /// story rather than noise.
    /// </summary>
    /// <remarks>
    /// Both conditions have to hold. The gap must reach <see cref="Floor"/>, and the processor must account for less
    /// than half the wall clock: a build that spends half its time waiting on storage is ordinary here, and a build
    /// that spends a twentieth of it there was not running.
    /// </remarks>
    internal bool Diverges => Wall - Processor >= Floor && Processor * 2 < Wall;

    /// <summary>
    /// Renders the two clocks the way the report line prints them.
    /// </summary>
    /// <returns>For example <c>10 m 32 s wall · 9 m 41 s cpu</c>.</returns>
    internal string Render() => string.Concat(Span(Wall), " wall · ", Span(Processor), " cpu");

    /// <summary>
    /// Explains why the two clocks disagree, or <see langword="null"/> when they agree closely enough.
    /// </summary>
    /// <returns>One line for the report, or <see langword="null"/>.</returns>
    internal string? Divergence => Diverges
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"the wall clock is {Factor()} the processor time — the machine was asleep or the process was never scheduled, not the index being slow; check the power events (Kernel-Power 506/507 under Modern Standby) before believing the duration")
        : null;

    /// <summary>
    /// Renders a duration at the precision its size deserves.
    /// </summary>
    /// <param name="value">The duration.</param>
    /// <returns><c>0.4 s</c>, <c>10 m 32 s</c> or <c>8 h 25 m</c>.</returns>
    /// <remarks>
    /// Shared with the refresh line rather than copied: a duration printed by two renderers would eventually be
    /// printed two ways, and the number a developer compares across runs is the number that has to match.
    /// </remarks>
    internal static string Span(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalSeconds switch
        {
            >= 3600 => string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours} h {value.Minutes} m"),
            >= 60 => string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalMinutes} m {value.Seconds} s"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.0} s"),
        };
    }

    /// <summary>
    /// Renders how many times larger the wall clock is than the processor time.
    /// </summary>
    /// <returns>For example <c>35×</c>, or <c>over 100×</c> when the processor time rounds to nothing.</returns>
    private string Factor()
    {
        var factor = Processor > TimeSpan.Zero ? Wall / Processor : double.PositiveInfinity;

        return double.IsFinite(factor) && factor < 100
            ? string.Create(CultureInfo.InvariantCulture, $"{factor:0}×")
            : "over 100×";
    }
}
