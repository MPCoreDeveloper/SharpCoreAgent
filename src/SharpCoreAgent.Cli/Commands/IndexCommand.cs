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
    private const int LabelWidth = 12;

    /// <summary>
    /// Gets the wall-clock horizon a command-line build gets.
    /// </summary>
    /// <remarks>
    /// The three-second budget belongs to a turn-start refresh, where a model is waiting. A command is a developer
    /// asking for the work, so it gets a horizon instead: long enough that nothing real hits it, short enough that a
    /// build which somehow never finishes still returns a report a script can branch on.
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
                var built = await index
                    .EnsureFreshAsync(CommandBudget, cancellationToken)
                    .ConfigureAwait(false);

                exitCode = Math.Max(
                    exitCode,
                    await ReportAsync(options, output, workspace, index, "build", "indexing", built, cancellationToken).ConfigureAwait(false));
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
                var report = await index.PruneAsync(cancellationToken).ConfigureAwait(false);
                exitCode = Math.Max(
                    exitCode,
                    await ReportAsync(options, output, workspace, index, "prune", "pruning", report, cancellationToken).ConfigureAwait(false));
            }

            if (options.IndexRebuild)
            {
                var report = await index.RebuildAsync(cancellationToken).ConfigureAwait(false);
                exitCode = Math.Max(
                    exitCode,
                    await ReportAsync(options, output, workspace, index, "rebuild", "indexing", report, cancellationToken).ConfigureAwait(false));
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
                ? string.Create(CultureInfo.InvariantCulture, $"index · partial ({Number(status.Files)} files indexed, {Seconds(report.ElapsedMilliseconds)})")
                : string.Create(CultureInfo.InvariantCulture, $"index · {State(status.Freshness)} ({Number(status.Files)} files, {Number(status.Chunks)} passages, {Seconds(report.ElapsedMilliseconds)})");

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
            WriteJson(output, new Dictionary<string, object?>
            {
                ["command"] = "index",
                ["action"] = "status",
                ["workspace"] = Slash(workspace.Root),
                ["state"] = State(status.Freshness),
                ["tier"] = TierName(status.Tier),
                ["embedder"] = status.Embedder,
                ["dimensions"] = status.Dimensions,
                ["files"] = status.Files,
                ["stale"] = status.StaleFiles,
                ["skipped"] = status.SkippedFiles,
                ["orphans"] = status.OrphanRows,
                ["passages"] = status.Chunks,
                ["symbols"] = status.Symbols,
                ["edges"] = status.Edges,
                ["communities"] = status.Communities,
                ["sizeBytes"] = status.SizeBytes,
                ["vectorSearch"] = status.VectorSearchEnabled,
                ["builtUtc"] = status.BuiltUtc?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["incomplete"] = status.Freshness is IndexFreshness.Partial,
                ["notes"] = Array.Empty<string>(),
            });

            return ExitCodes.Success;
        }

        var builder = new StringBuilder();
        builder.Append("index · sharpcoreagent @ ").Append(Slash(workspace.Root)).Append('\n');

        if (status.Freshness == IndexFreshness.Missing)
        {
            builder.Append(Line("state", "missing")).Append('\n');
            builder.Append(Line("hint", "run `sharpcoreagent index` to build it"));
            await output.WriteLineAsync(builder, cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        builder
            .Append(Line("state", State(status.Freshness))).Append('\n')
            .Append(Line("tier", Describe(status))).Append('\n')
            .Append(Line("built", status.BuiltUtc is { } built
                ? built.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : "never")).Append('\n')
            .Append(Line("files", string.Create(
                CultureInfo.InvariantCulture,
                $"{Number(status.Files)} indexed · {status.StaleFiles} stale · {status.SkippedFiles} skipped · {status.OrphanRows} orphan rows"))).Append('\n')
            .Append(Line("content", string.Create(
                CultureInfo.InvariantCulture,
                $"{Number(status.Chunks)} passages · {Number(status.Symbols)} symbols · {Number(status.Edges)} edges"))).Append('\n')
            .Append(Line("size", $"{Size(status.SizeBytes)} in {Directory(workspace, index)}")).Append('\n')
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
                ["workspace"] = Slash(workspace.Root),
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
        builder.Append("verify · ").Append(Slash(workspace.Root)).Append('\n');
        builder.Append(Line("files", string.Create(
            CultureInfo.InvariantCulture,
            $"{Number(status.Files)} in index · {Number(onDisk)} on disk   (+{verification.FilesMissingFromIndex} missing from index)"))).Append('\n');
        builder.Append(Line("hashes", string.Create(
            CultureInfo.InvariantCulture,
            $"{verification.HashMismatches} mismatch{(verification.HashMismatches == 1 ? string.Empty : "es")}"))).Append('\n');
        builder.Append(Line("orphans", string.Create(
            CultureInfo.InvariantCulture,
            $"{Number(verification.Orphans)} rows"))).Append('\n');
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
    /// Prints the result of a build or a prune.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="workspace">The workspace that was indexed.</param>
    /// <param name="index">The open index, whose directory the report names.</param>
    /// <param name="action">The JSON action name: <c>build</c>, <c>rebuild</c> or <c>prune</c>.</param>
    /// <param name="verb">The word the prose form starts with: <c>indexing</c> or <c>pruning</c>.</param>
    /// <param name="report">What the work did.</param>
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
        CancellationToken cancellationToken)
    {
        var exitCode = report.Incomplete ? ExitCodes.Failed : ExitCodes.Success;

        if (options.Json)
        {
            WriteJson(output, new Dictionary<string, object?>
            {
                ["command"] = "index",
                ["action"] = action,
                ["workspace"] = Slash(workspace.Root),
                ["state"] = State(report.Status.Freshness),
                ["tier"] = TierName(report.Status.Tier),
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
                ["incomplete"] = report.Incomplete,
                ["notes"] = report.Notes,
            });

            return exitCode;
        }

        var scanned = report.Added + report.Changed + report.Removed + report.Unchanged;
        var builder = new StringBuilder();

        builder.Append(verb).Append(' ').Append(Slash(workspace.Root))
            .Append(string.Create(CultureInfo.InvariantCulture, $" ({TierName(report.Status.Tier)}, {report.Status.Embedder})")).Append('\n');

        builder.Append(Line("scanned", string.Create(
            CultureInfo.InvariantCulture,
            $"{Number(scanned)} files · {report.Added} added · {report.Changed} changed · {report.Removed} removed · {report.Unchanged} unchanged · {report.Skipped} skipped"))).Append('\n');

        builder.Append(Line("content", string.Create(
            CultureInfo.InvariantCulture,
            $"{Number(report.Status.Chunks)} passages · {Number(report.Status.Symbols)} symbols · {Number(report.Status.Edges)} edges"))).Append('\n');

        builder.Append(Line("communities", report.Status.Communities == 0
            ? "none · no declaration has a relation yet"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Number(report.Status.Communities)} · centers {Number(report.Status.Centers)}"))).Append('\n');

        builder.Append(Line("wrote", string.Create(
            CultureInfo.InvariantCulture,
            $"{Directory(workspace, index)} in {Seconds(report.ElapsedMilliseconds)} ({Size(report.Status.SizeBytes)})"))).Append('\n');

        builder.Append(Line("state", report.Incomplete
            ? "partial · run `sharpcoreagent index` to finish"
            : State(report.Status.Freshness)));

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
    /// Renders one labelled line of the status blocks.
    /// </summary>
    /// <param name="label">The word in the left column.</param>
    /// <param name="value">The text it describes.</param>
    /// <returns>Two spaces, the padded label, then the value.</returns>
    private static string Line(string label, string value) => string.Concat("  ", label.PadRight(LabelWidth), value);

    /// <summary>
    /// Renders a count with groups of three separated by a space.
    /// </summary>
    /// <param name="value">The count.</param>
    /// <returns>For example <c>1 908</c>.</returns>
    private static string Number(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    /// <summary>
    /// Renders a byte count with one decimal place.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>For example <c>14.2 MiB</c>.</returns>
    private static string Size(long bytes) => bytes switch
    {
        < 0 => "0 B",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.0} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.0} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):0.0} GiB"),
    };

    /// <summary>
    /// Renders a duration in seconds.
    /// </summary>
    /// <param name="milliseconds">The elapsed time.</param>
    /// <returns>For example <c>0.4 s</c>.</returns>
    private static string Seconds(long milliseconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1000d:0.0} s");

    /// <summary>
    /// Renders the freshness word the blocks use.
    /// </summary>
    /// <param name="freshness">The state to render.</param>
    /// <returns>The lowercase name.</returns>
    private static string State(IndexFreshness freshness) => freshness switch
    {
        IndexFreshness.Missing => "missing",
        IndexFreshness.Building => "building",
        IndexFreshness.Partial => "partial",
        IndexFreshness.Stale => "stale",
        IndexFreshness.Fresh => "fresh",
        IndexFreshness.Corrupt => "corrupt",
        _ => "unknown",
    };

    /// <summary>
    /// Renders the tier name the output uses.
    /// </summary>
    /// <param name="tier">The tier to render.</param>
    /// <returns><c>lean</c> or <c>rich</c>.</returns>
    private static string TierName(IndexTier tier) => tier == IndexTier.Rich ? "rich" : "lean";

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

        return string.Create(CultureInfo.InvariantCulture, $"{TierName(status.Tier)} · embedder {status.Embedder}{dimensions}");
    }

    /// <summary>
    /// Renders the index directory the way the status block names it.
    /// </summary>
    /// <param name="workspace">The workspace the index belongs to.</param>
    /// <param name="index">The open index.</param>
    /// <returns>For example <c>.sharpcoreagent/index</c>.</returns>
    private static string Directory(Workspace workspace, SharpCoreAgentIndex index) =>
        Slash(workspace.ToRelative(index.IndexDirectory));

    /// <summary>
    /// Renders a path with forward slashes, which is how every block prints one.
    /// </summary>
    /// <param name="path">The path to render.</param>
    /// <returns>The path with <c>/</c> separators.</returns>
    private static string Slash(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Writes one JSON object on its own line.
    /// </summary>
    /// <param name="output">Where the object goes.</param>
    /// <param name="fields">The object's keys and values, in the order they must appear.</param>
    private static void WriteJson(TextWriter output, Dictionary<string, object?> fields)
    {
        output.WriteLine(JsonSerializer.Serialize(fields));
    }
}
