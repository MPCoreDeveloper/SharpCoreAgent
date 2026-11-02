using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Implements <c>sharpcoreagent index</c>: build it, look at it, check it, or repair it.
/// </summary>
/// <remarks>
/// The command owns a real <see cref="SharpCoreAgentIndex"/> and disposes it, which is what releases the writer lock for
/// the next process. Every action goes through the same handle so the status a build reports and the status a later
/// <c>--status</c> prints come from one code path.
/// <para>
/// Writing is deliberately boring: the index writer already decides what changed, so this file only chooses which
/// method to call and turns the returned report into prose or one JSON object. Nothing here reads the database directly,
/// which is why a future engine can replace the store without touching the command.
/// </para>
/// </remarks>
internal static class IndexCommand
{
    /// <summary>The width the label column of every status block is padded to.</summary>
    private const int LabelWidth = 12;

    /// <summary>
    /// The smallest remainder the phase block prints as <c>other</c>.
    /// </summary>
    /// <remarks>
    /// Below a twentieth of a second the difference between a run and the sum of its phases is rounding, and a line
    /// that appears only to say <c>0.0 s</c> teaches the reader to skip the block.
    /// </remarks>
    private static readonly TimeSpan OtherFloor = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets the processor time a command-line build gets.
    /// </summary>
    /// <remarks>
    /// The three-second budget belongs to a turn-start refresh, where a model is waiting. A command is a developer
    /// asking for the work, so it gets a horizon instead: long enough that nothing real hits it, short enough that a
    /// build which somehow never finishes still returns a report a script can branch on.
    /// <para>
    /// It is a horizon of <em>work</em>, not of clock, and that distinction was learned the hard way: the same hour
    /// once measured wall clock, a laptop spent eight hours of one run in Modern Standby, and a ten-minute build
    /// stopped a sixth of the way through with a report that blamed the index (spec 002, D19). Processor time
    /// cannot be spent by a machine that is not running this process.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan CommandBudget = TimeSpan.FromHours(1);

    /// <summary>
    /// Runs the requested action.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="cancellationToken">Cancels a build; the index stays usable either way.</param>
    /// <returns>The documented exit code.</returns>
    /// <exception cref="CliUsageException">The requested embedder or tier is not produced by this build.</exception>
    public static Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken) =>
        ExecuteAsync(options, Console.Out, Console.Error, cancellationToken);

    /// <summary>
    /// Runs the requested action, writing the report and the diagnosis to two streams.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the report goes: stdout for the process, a buffer for a test.</param>
    /// <param name="error">Where a diagnosis goes: stderr for the process, a buffer for a test.</param>
    /// <param name="cancellationToken">Cancels a build; the index stays usable either way.</param>
    /// <returns>The documented exit code.</returns>
    /// <exception cref="CliUsageException">The requested embedder or tier is not produced by this build.</exception>
    /// <remarks>
    /// The writers are parameters because the exit code, the JSON shape and the failure message are all part of the
    /// contract, and a test that can see none of them has to either drive the process or give up. The split is kept: a
    /// script piping the report wants stdout to stay parseable, so everything that is not the report goes to stderr.
    /// </remarks>
    internal static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        RejectUnsupported(options);

        if (!CliHost.TryOpenWorkspace(options, error, out var opened))
        {
            return ExitCodes.Usage;
        }

        var workspace = opened!;
        await using var index = SharpCoreAgentIndex.Open(workspace);

        var wanted = options.IndexStatus || options.IndexRebuild || options.IndexVerify || options.IndexPrune;
        var exitCode = ExitCodes.Success;

        try
        {
            if (!wanted)
            {
                // A bare `index` builds, which is what the "run `sharpcoreagent index`" hint in every other message
                // promises. It is incremental, so running it against a fresh index costs one scan and reports zeros.
                var (built, timing) = await MeasureAsync(() =>
                    index.EnsureFreshAsync(CommandBudget, IndexBudgetClock.ProcessorTime, cancellationToken)).ConfigureAwait(false);

                exitCode = Math.Max(
                    exitCode,
                    await ReportAsync(options, output, workspace, index, "build", "indexing", built, timing, cancellationToken).ConfigureAwait(false));
            }

            if (options.IndexStatus)
            {
                exitCode = Math.Max(
                    exitCode,
                    await StatusAsync(options, output, workspace, index, cancellationToken).ConfigureAwait(false));
            }

            if (options.IndexVerify)
            {
                exitCode = Math.Max(
                    exitCode,
                    await VerifyAsync(options, output, workspace, index, cancellationToken).ConfigureAwait(false));
            }

            if (options.IndexPrune)
            {
                var (report, timing) = await MeasureAsync(() => index.PruneAsync(cancellationToken)).ConfigureAwait(false);
                exitCode = Math.Max(
                    exitCode,
                    await ReportAsync(options, output, workspace, index, "prune", "pruning", report, timing, cancellationToken).ConfigureAwait(false));
            }

            if (options.IndexRebuild)
            {
                var (report, timing) = await MeasureAsync(() =>
                    index.RebuildAsync(IndexBudgetClock.ProcessorTime, cancellationToken)).ConfigureAwait(false);
                exitCode = Math.Max(
                    exitCode,
                    await ReportAsync(options, output, workspace, index, "rebuild", "indexing", report, timing, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The index could not be created or written: not a transient condition, and not the same failure as a
            // build that stopped early, which is why it has its own exit code.
            await error.WriteLineAsync($"error: {ex.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return ExitCodes.IndexUnavailable;
        }

        return exitCode;
    }

    /// <summary>
    /// Brings the index up to date before a turn reads it, and says so when anything changed.
    /// </summary>
    /// <param name="provider">The container the turn runs in.</param>
    /// <param name="options">The parsed command line: the budget and whether the index is on.</param>
    /// <param name="quiet">Whether the summary line is suppressed.</param>
    /// <param name="output">Where the summary goes.</param>
    /// <param name="cancellationToken">Cancels the refresh without failing the turn.</param>
    /// <returns>A task that completes when the index is as fresh as the budget allows.</returns>
    /// <remarks>
    /// This is the turn-start half of the index contract, and it is deliberately forgiving. A refresh can fail — the
    /// directory may be read-only, another process may hold the writer lock, the disk may be full — and none of those
    /// may end a turn: the five tools then answer with the failure that names the problem and the model falls back to
    /// <c>search_text</c>. Only cancellation propagates, because the developer asked for it.
    /// <para>
    /// The line is printed only when something changed. A steady session therefore says nothing after the first turn,
    /// while a turn that wrote a file with <c>write_file</c> announces the refresh it caused.
    /// </para>
    /// </remarks>
    public static async Task RefreshAsync(
        IServiceProvider provider,
        CliOptions options,
        bool quiet,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        // A zero budget means "read the index, never refresh it", and --no-index means no maintainer was registered at
        // all: both cases cost one lookup and no I/O.
        if (options.IndexBudgetMs <= 0 || provider.GetService<ICodeIndexMaintainer>() is not { } maintainer)
        {
            return;
        }

        try
        {
            var report = await maintainer
                .EnsureFreshAsync(TimeSpan.FromMilliseconds(options.IndexBudgetMs), cancellationToken)
                .ConfigureAwait(false);

            if (quiet)
            {
                return;
            }

            var changed = report.Added + report.Changed + report.Removed;

            if (changed == 0 && !report.Incomplete && report.Status.Freshness == IndexFreshness.Fresh)
            {
                return;
            }

            var status = report.Status;
            var line = report.Incomplete
                ? string.Create(CultureInfo.InvariantCulture, $"index · partial ({IndexStatusDocument.Number(status.Files)} files indexed, {Seconds(report.ElapsedMilliseconds)})")
                : string.Create(CultureInfo.InvariantCulture, $"index · {IndexStatusDocument.State(status.Freshness)} ({IndexStatusDocument.Number(status.Files)} files, {IndexStatusDocument.Number(status.Chunks)} passages, {Seconds(report.ElapsedMilliseconds)})");

            await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!quiet)
            {
                await output.WriteLineAsync(
                    string.Create(CultureInfo.InvariantCulture, $"index · unavailable ({ex.Message})").AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Refuses a requested embedder or tier this build does not produce.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <exception cref="CliUsageException">
    /// <c>--embedder</c> names anything but <c>hashed-local</c>, or <c>--tier</c> names <c>rich</c>.
    /// </exception>
    /// <remarks>
    /// The options are parsed and validated first, then refused here, because a silent "build what I can" would leave a
    /// developer believing a provider embedding had been computed. The message names what this build does make.
    /// </remarks>
    private static void RejectUnsupported(CliOptions options)
    {
        if (options.Embedder != EmbedderKind.Local)
        {
            throw new CliUsageException(
                $"this build only produces the hashed-local embedder, but --embedder {Manifest(options.Embedder)} was requested.",
                "Use --embedder hashed-local, or drop the option.");
        }

        if (options.Tier != IndexTier.Lean)
        {
            throw new CliUsageException(
                "this build only produces the lean tier, but --tier rich was requested.",
                "Use --tier lean, or drop the option.");
        }
    }

    /// <summary>
    /// Prints the coverage block.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the block goes.</param>
    /// <param name="workspace">The workspace the index describes.</param>
    /// <param name="index">The open index.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Always <see cref="ExitCodes.Success"/>: printing a status has no failure mode beyond an exception.</returns>
    private static async Task<int> StatusAsync(
        CliOptions options,
        TextWriter output,
        Workspace workspace,
        SharpCoreAgentIndex index,
        CancellationToken cancellationToken)
    {
        var status = await index.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (options.Json)
        {
            // One builder for both surfaces: `ui --serve`'s /api/status.json answers with this same object, which is
            // what makes the two renderings comparable byte for byte instead of field by field.
            WriteJson(output, IndexStatusDocument.Build(workspace, status));

            return ExitCodes.Success;
        }

        var builder = new StringBuilder();
        builder.Append("index · sharpcoreagent @ ").Append(IndexStatusDocument.Slash(workspace.Root)).Append('\n');

        if (status.Freshness == IndexFreshness.Missing)
        {
            builder.Append(Line("state", "missing")).Append('\n');
            builder.Append(Line("hint", "run `sharpcoreagent index` to build it"));
            await output.WriteLineAsync(builder, cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        builder
            .Append(Line("state", IndexStatusDocument.State(status.Freshness))).Append('\n')
            .Append(Line("tier", Describe(status))).Append('\n')
            .Append(Line("built", status.BuiltUtc is { } built
                ? built.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : "never")).Append('\n')
            .Append(Line("files", string.Create(
                CultureInfo.InvariantCulture,
                $"{IndexStatusDocument.Number(status.Files)} indexed · {status.StaleFiles} stale · {status.SkippedFiles} skipped · {status.OrphanRows} orphan rows"))).Append('\n')
            .Append(Line("content", string.Create(
                CultureInfo.InvariantCulture,
                $"{IndexStatusDocument.Number(status.Chunks)} passages · {IndexStatusDocument.Number(status.Symbols)} symbols · {IndexStatusDocument.Number(status.Edges)} edges"))).Append('\n')
            .Append(Line("size", $"{IndexStatusDocument.Size(status.SizeBytes)} in {Directory(workspace, index)}")).Append('\n')
            .Append(Line("retrieval", string.Create(
                CultureInfo.InvariantCulture,
                $"lexical on · vector {(status.VectorSearchEnabled && !status.LexicalFallback ? "on" : "off")} · graph on")));

        await output.WriteLineAsync(builder, cancellationToken).ConfigureAwait(false);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Compares the index against the workspace and prints every disagreement.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="workspace">The workspace the index describes.</param>
    /// <param name="index">The open index.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns><see cref="ExitCodes.Success"/> when nothing drifted, <see cref="ExitCodes.Failed"/> otherwise.</returns>
    /// <remarks>
    /// A drift result exits non-zero because a script that checks an index before a build wants that answer in its
    /// control flow, and because the alternative — a green exit with a "drift" line hidden in the output — is exactly the
    /// failure this check exists to catch.
    /// </remarks>
    private static async Task<int> VerifyAsync(
        CliOptions options,
        TextWriter output,
        Workspace workspace,
        SharpCoreAgentIndex index,
        CancellationToken cancellationToken)
    {
        var verification = await index.VerifyAsync(cancellationToken).ConfigureAwait(false);
        var status = await index.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var exitCode = verification.IsHealthy ? ExitCodes.Success : ExitCodes.Failed;

        if (options.Json)
        {
            WriteJson(output, new Dictionary<string, object?>
            {
                ["command"] = "index",
                ["action"] = "verify",
                ["workspace"] = IndexStatusDocument.Slash(workspace.Root),
                ["healthy"] = verification.IsHealthy,
                ["files"] = status.Files,
                ["filesMissingFromIndex"] = verification.FilesMissingFromIndex,
                ["filesMissingOnDisk"] = verification.FilesMissingOnDisk,
                ["hashMismatches"] = verification.HashMismatches,
                ["orphans"] = verification.Orphans,
                ["details"] = verification.Details,
            });

            return exitCode;
        }

        var onDisk = Math.Max(0, status.Files + verification.FilesMissingFromIndex - verification.FilesMissingOnDisk);

        var builder = new StringBuilder();
        builder.Append("verify · ").Append(IndexStatusDocument.Slash(workspace.Root)).Append('\n');
        builder.Append(Line("files", string.Create(
            CultureInfo.InvariantCulture,
            $"{IndexStatusDocument.Number(status.Files)} in index · {IndexStatusDocument.Number(onDisk)} on disk   (+{verification.FilesMissingFromIndex} missing from index)"))).Append('\n');
        builder.Append(Line("hashes", string.Create(
            CultureInfo.InvariantCulture,
            $"{verification.HashMismatches} mismatch{(verification.HashMismatches == 1 ? string.Empty : "es")}"))).Append('\n');
        builder.Append(Line("orphans", string.Create(
            CultureInfo.InvariantCulture,
            $"{IndexStatusDocument.Number(verification.Orphans)} rows"))).Append('\n');
        builder.Append(Line("result", verification.IsHealthy ? "fresh" : "drift"));

        foreach (var detail in verification.Details.Take(20))
        {
            builder.Append('\n').Append("  ").Append(detail);
        }

        if (verification.Details.Count > 20)
        {
            builder.Append('\n').Append(string.Create(
                CultureInfo.InvariantCulture,
                $"  … {verification.Details.Count - 20} more"));
        }

        if (!verification.IsHealthy)
        {
            builder.Append('\n').Append(Line("hint", "run `sharpcoreagent index` (or `--rebuild`) to repair"));
        }

        await output.WriteLineAsync(builder, cancellationToken).ConfigureAwait(false);

        return exitCode;
    }

    /// <summary>
    /// Runs one write action, measuring the processor time it cost beside the wall clock it reports.
    /// </summary>
    /// <param name="work">The action to run.</param>
    /// <returns>The report and the two clocks over the same window.</returns>
    /// <remarks>
    /// The wall clock is taken from the report rather than from a second stopwatch here: the maintainer already
    /// measures from before the wait for the writer lock, and two stopwatches would be two answers to one question.
    /// The processor time is read around the call, which is the same window, and it is process-wide because a
    /// command is one process doing one thing.
    /// </remarks>
    private static async Task<(IndexBuildReport Report, BuildTiming Timing)> MeasureAsync(
        Func<ValueTask<IndexBuildReport>> work)
    {
        var processorStarted = Environment.CpuUsage;
        var report = await work().ConfigureAwait(false);

        return (report, new BuildTiming(
            TimeSpan.FromMilliseconds(report.ElapsedMilliseconds),
            Environment.CpuUsage.TotalTime - processorStarted.TotalTime));
    }

    /// <summary>
    /// Prints the result of a build or a prune.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="workspace">The workspace that was indexed.</param>
    /// <param name="index">The open index, whose directory the report names.</param>
    /// <param name="action">The JSON action name: <c>build</c>, <c>rebuild</c> or <c>prune</c>.</param>
    /// <param name="verb">The word the prose form starts with: <c>indexing</c> or <c>pruning</c>.</param>
    /// <param name="report">What the work did.</param>
    /// <param name="timing">The wall clock the report carries, beside the processor time the same window cost.</param>
    /// <param name="cancellationToken">Unused; kept so the signature matches the other actions.</param>
    /// <returns><see cref="ExitCodes.Success"/>, or <see cref="ExitCodes.Failed"/> when the build stopped early.</returns>
    private static async Task<int> ReportAsync(
        CliOptions options,
        TextWriter output,
        Workspace workspace,
        SharpCoreAgentIndex index,
        string action,
        string verb,
        IndexBuildReport report,
        BuildTiming timing,
        CancellationToken cancellationToken)
    {
        var exitCode = report.Incomplete ? ExitCodes.Failed : ExitCodes.Success;

        if (options.Json)
        {
            WriteJson(output, new Dictionary<string, object?>
            {
                ["command"] = "index",
                ["action"] = action,
                ["workspace"] = IndexStatusDocument.Slash(workspace.Root),
                ["state"] = IndexStatusDocument.State(report.Status.Freshness),
                ["tier"] = IndexStatusDocument.TierName(report.Status.Tier),
                ["embedder"] = report.Status.Embedder,
                ["dimensions"] = report.Status.Dimensions,
                ["files"] = report.Status.Files,
                ["stale"] = report.Status.StaleFiles,
                ["skipped"] = report.Skipped,
                ["passages"] = report.Status.Chunks,
                ["symbols"] = report.Status.Symbols,
                ["edges"] = report.Status.Edges,
                ["communities"] = report.Status.Communities,
                ["sizeBytes"] = report.Status.SizeBytes,
                ["added"] = report.Added,
                ["changed"] = report.Changed,
                ["removed"] = report.Removed,
                ["unchanged"] = report.Unchanged,
                ["elapsedMs"] = report.ElapsedMilliseconds,
                ["cpuMs"] = (long)timing.Processor.TotalMilliseconds,
                ["phases"] = report.Phases
                    .Select(static phase => new Dictionary<string, object?>
                    {
                        ["name"] = phase.Name,
                        ["wallMs"] = (long)phase.Wall.TotalMilliseconds,
                        ["cpuMs"] = (long)phase.Processor.TotalMilliseconds,
                        ["nested"] = phase.Nested,
                    })
                    .ToArray(),
                ["incomplete"] = report.Incomplete,
                ["notes"] = report.Notes,
            });

            return exitCode;
        }

        var scanned = report.Added + report.Changed + report.Removed + report.Unchanged;
        var builder = new StringBuilder();

        builder.Append(verb).Append(' ').Append(IndexStatusDocument.Slash(workspace.Root))
            .Append(string.Create(CultureInfo.InvariantCulture, $" ({IndexStatusDocument.TierName(report.Status.Tier)}, {report.Status.Embedder})")).Append('\n');

        builder.Append(Line("scanned", string.Create(
            CultureInfo.InvariantCulture,
            $"{IndexStatusDocument.Number(scanned)} files · {report.Added} added · {report.Changed} changed · {report.Removed} removed · {report.Unchanged} unchanged · {report.Skipped} skipped"))).Append('\n');

        builder.Append(Line("content", string.Create(
            CultureInfo.InvariantCulture,
            $"{IndexStatusDocument.Number(report.Status.Chunks)} passages · {IndexStatusDocument.Number(report.Status.Symbols)} symbols · {IndexStatusDocument.Number(report.Status.Edges)} edges"))).Append('\n');

        builder.Append(Line("communities", report.Status.Communities == 0
            ? "none · no declaration has a relation yet"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{IndexStatusDocument.Number(report.Status.Communities)} · centers {IndexStatusDocument.Number(report.Status.Centers)}"))).Append('\n');

        builder.Append(Line("wrote", string.Create(
            CultureInfo.InvariantCulture,
            $"{Directory(workspace, index)} in {timing.Render()} ({IndexStatusDocument.Size(report.Status.SizeBytes)})"))).Append('\n');

        // Only when the two clocks disagree enough to be the story: a line that fired on every ordinary build
        // would be a line nobody reads.
        if (timing.Divergence is { } divergence)
        {
            builder.Append(Line("clock", divergence)).Append('\n');
        }

        // Behind --verbose, because a build that costs five seconds does not need eight lines to explain them, and the
        // run that needs them is the one a developer is already asking about (spec 002, D18: one total cannot say
        // which step of a build the time went into).
        if (options.Verbose && report.Phases.Count > 0)
        {
            builder.Append(Phases(report.Phases, timing));
        }

        builder.Append(Line("state", report.Incomplete
            ? "partial · run `sharpcoreagent index` to finish"
            : IndexStatusDocument.State(report.Status.Freshness)));

        foreach (var note in report.Notes.Take(10))
        {
            builder.Append('\n').Append("  ").Append(note);
        }

        if (report.Notes.Count > 10)
        {
            builder.Append('\n').Append(string.Create(
                CultureInfo.InvariantCulture,
                $"  … {report.Notes.Count - 10} more notes"));
        }

        await output.WriteLineAsync(builder, cancellationToken).ConfigureAwait(false);

        return exitCode;
    }

    /// <summary>
    /// Renders the phase block: one line per step of the build, both clocks, and the share of the run it took.
    /// </summary>
    /// <param name="phases">The steps, in the order the build ran them.</param>
    /// <param name="timing">The whole run, which is the denominator and the source of the unaccounted-for remainder.</param>
    /// <returns>Zero or more terminated lines.</returns>
    /// <remarks>
    /// The last line is the difference between the run and its steps — the wait for the writer lock, opening the
    /// store, reading the status back — printed only when it is big enough to matter. Without it the percentages
    /// would quietly fail to add up, which is the failure this whole block exists to end.
    /// <para>
    /// A nested step takes its number without a share, because its time is already inside the step above it: two
    /// percentages that describe the same seconds would read as a hundred and ninety percent of a run.
    /// </para>
    /// </remarks>
    private static string Phases(IReadOnlyList<IndexPhase> phases, BuildTiming timing)
    {
        var labels = phases.Select(static phase => phase.Nested ? "of which " + phase.Name : phase.Name).ToList();
        var labelWidth = labels.Max(static label => label.Length);
        var wallWidth = phases.Max(static phase => BuildTiming.Span(phase.Wall).Length);
        var cpuWidth = phases.Max(static phase => BuildTiming.Span(phase.Processor).Length);

        // Both number columns are padded to their widest cell, so the seconds line up and the eye can compare them.
        string Clocks(TimeSpan wall, TimeSpan processor) => string.Create(
            CultureInfo.InvariantCulture,
            $"{BuildTiming.Span(wall).PadLeft(wallWidth)} wall · {BuildTiming.Span(processor).PadLeft(cpuWidth)} cpu");

        var builder = new StringBuilder();
        var accountedWall = TimeSpan.Zero;
        var accountedProcessor = TimeSpan.Zero;

        for (var index = 0; index < phases.Count; index++)
        {
            var phase = phases[index];
            var row = labels[index].PadRight(labelWidth) + "  " + Clocks(phase.Wall, phase.Processor);

            if (!phase.Nested)
            {
                accountedWall += phase.Wall;
                accountedProcessor += phase.Processor;
                row += string.Create(CultureInfo.InvariantCulture, $"  {Share(phase.Wall, timing.Wall),4:0}%");
            }

            builder.Append(index == 0 ? Line("phases", row) : Line(string.Empty, row)).Append('\n');
        }

        var otherWall = timing.Wall - accountedWall;
        var otherProcessor = timing.Processor - accountedProcessor;
        if (otherProcessor < TimeSpan.Zero)
        {
            otherProcessor = TimeSpan.Zero;
        }

        if (otherWall > OtherFloor || otherProcessor > OtherFloor)
        {
            var other = "other".PadRight(labelWidth) + "  " + Clocks(otherWall, otherProcessor)
                + string.Create(CultureInfo.InvariantCulture, $"  {Share(otherWall, timing.Wall),4:0}%");

            builder.Append(Line(string.Empty, other)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders one step's share of the run, as a whole percentage.
    /// </summary>
    /// <param name="value">The step's wall clock.</param>
    /// <param name="total">The run's wall clock.</param>
    /// <returns>For example <c>94</c>; zero when the run measured nothing.</returns>
    private static int Share(TimeSpan value, TimeSpan total) =>
        total > TimeSpan.Zero
            ? (int)Math.Round(Math.Max(0, value / total) * 100, MidpointRounding.AwayFromZero)
            : 0;

    /// <summary>
    /// Renders one labelled line of the status blocks.
    /// </summary>
    /// <param name="label">The word in the left column.</param>
    /// <param name="value">The text it describes.</param>
    /// <returns>Two spaces, the padded label, then the value.</returns>
    private static string Line(string label, string value) => string.Concat("  ", label.PadRight(LabelWidth), value);

    /// <summary>
    /// Renders a duration in milliseconds.
    /// </summary>
    /// <param name="milliseconds">The elapsed time.</param>
    /// <returns>For example <c>0.4 s</c>.</returns>
    /// <remarks>
    /// The rendering itself lives on <see cref="BuildTiming.Span(TimeSpan)"/> so the refresh line and the build report
    /// print one duration one way.
    /// </remarks>
    private static string Seconds(long milliseconds) =>
        BuildTiming.Span(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// Renders the embedder name a usage error quotes back.
    /// </summary>
    /// <param name="embedder">The requested embedder.</param>
    /// <returns><c>provider</c> or <c>off</c>.</returns>
    private static string Manifest(EmbedderKind embedder) => embedder == EmbedderKind.Provider ? "provider" : "off";

    /// <summary>
    /// Renders the one-line description of how the index is built.
    /// </summary>
    /// <param name="status">The status to describe.</param>
    /// <returns>For example <c>lean · embedder hashed-local/512</c>.</returns>
    private static string Describe(IndexStatus status)
    {
        var dimensions = status.Dimensions > 0
            ? string.Create(CultureInfo.InvariantCulture, $" · dimensions {status.Dimensions}")
            : string.Empty;

        return string.Create(CultureInfo.InvariantCulture, $"{IndexStatusDocument.TierName(status.Tier)} · embedder {status.Embedder}{dimensions}");
    }

    /// <summary>
    /// Renders the index directory the way the status block names it.
    /// </summary>
    /// <param name="workspace">The workspace the index belongs to.</param>
    /// <param name="index">The open index.</param>
    /// <returns>For example <c>.sharpcoreagent/index</c>.</returns>
    private static string Directory(Workspace workspace, SharpCoreAgentIndex index) =>
        IndexStatusDocument.Slash(workspace.ToRelative(index.IndexDirectory));

    /// <summary>
    /// Writes one JSON object on its own line.
    /// </summary>
    /// <param name="output">Where the object goes.</param>
    /// <param name="fields">The object's keys and values, in the order they must appear.</param>
    /// <remarks>
    /// The serialisation is <see cref="IndexStatusDocument.Serialize"/>'s, so a line this command prints and the body
    /// the panel answers with cannot differ by a space.
    /// </remarks>
    private static void WriteJson(TextWriter output, Dictionary<string, object?> fields)
    {
        output.WriteLine(IndexStatusDocument.Serialize(fields));
    }
}
