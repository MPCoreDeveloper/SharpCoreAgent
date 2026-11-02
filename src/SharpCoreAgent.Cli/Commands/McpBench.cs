using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.McpServer;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Cli;

/// <summary>
/// One query's row: what the answer cost, what the files it cites cost, and how long the call took.
/// </summary>
/// <param name="Query">The question as the file asked it.</param>
/// <param name="AnswerChars">Characters of evidence the model would have read.</param>
/// <param name="SourceChars">Characters of the distinct files that answer cites, counted once each.</param>
/// <param name="Milliseconds">Wall-clock of the <c>tools/call</c> over stdio, handshake excluded.</param>
internal sealed record BenchRow(string Query, long AnswerChars, long SourceChars, int Milliseconds)
{
    /// <summary>Gets the evidence-to-source ratio for this query.</summary>
    public double Ratio => SourceChars == 0 ? 0 : (double)AnswerChars / SourceChars;
}

/// <summary>
/// The whole run: the two numbers SC-006 and SC-005 are recorded as, and whether parity held.
/// </summary>
/// <param name="Rows">The queries that answered identically on both sides.</param>
/// <param name="Attempted">How many were asked, so a mismatch cannot shrink the denominator.</param>
internal sealed record BenchReport(IReadOnlyList<BenchRow> Rows, int Attempted)
{
    /// <summary>Gets the characters every answer spent.</summary>
    public long AnswerChars => Rows.Sum(row => row.AnswerChars);

    /// <summary>Gets the characters of the distinct cited files, counted once each per query.</summary>
    public long SourceChars => Rows.Sum(row => row.SourceChars);

    /// <summary>Gets the evidence-to-source ratio (SC-006): below one means the answer is cheaper than the files.</summary>
    public double Ratio => SourceChars == 0 ? 0 : (double)AnswerChars / SourceChars;

    /// <summary>Gets how many queries answered the same text in process and over stdio (SC-002).</summary>
    public int Identical => Rows.Count;

    /// <summary>Gets a value indicating whether every query answered identically.</summary>
    public bool IsComplete => Rows.Count == Attempted;

    /// <summary>Gets the 95th percentile of the stdio call times (SC-005), by nearest rank.</summary>
    /// <remarks>
    /// Nearest rank rather than an interpolation: with twelve samples the p95 <em>is</em> the slowest one, and
    /// pretending otherwise would publish a number no call ever took.
    /// </remarks>
    public int P95Milliseconds
    {
        get
        {
            if (Rows.Count == 0)
            {
                return 0;
            }

            var sorted = Rows.Select(row => row.Milliseconds).Order().ToArray();
            var rank = (int)Math.Ceiling(0.95 * sorted.Length);
            return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
        }
    }
}

/// <summary>
/// Implements <c>sharpcoreagent mcp bench</c>: the fixture questions, asked twice, measured once.
/// </summary>
/// <remarks>
/// <para>
/// This is the round's own measurement, and it is a command rather than a script for the reason the index command
/// exists: it runs wherever the product runs, and it goes through the tool code path instead of reimplementing it. Both
/// halves of every query are the product's own — <c>search_code</c> from the container <c>mcp serve</c> registers, and
/// the same tool over stdio through this product's client — so the ratio it prints is the ratio a client would get.
/// </para>
/// <para>
/// A query whose two answers differ is a failure, not a column: parity is the point of asking twice, and a bench that
/// averaged a mismatch away would be publishing a number about a server that is not this one.
/// </para>
/// </remarks>
internal static class McpBench
{
    /// <summary>Where the questions live, relative to the workspace root.</summary>
    /// <remarks>
    /// In the spec folder rather than in the product's own configuration: the file is an artefact of the round that
    /// measured it, checked in beside the numbers it produced, and named here so the refusal below can quote it.
    /// </remarks>
    internal const string QueriesRelativePath = "specs/013-evidence-mcp-server/bench/queries.txt";

    /// <summary>
    /// Runs every question in the fixture file and prints the table the numbers in <c>plan.md</c> are pasted from.
    /// </summary>
    /// <param name="options">The parsed command line: the workspace.</param>
    /// <param name="output">Where the table goes, and nothing else.</param>
    /// <param name="error">Where the header, the parity failures and the diagnostics go.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>0 when every query answered identically, 1 when one did not, 2 when the run could not be set up.</returns>
    internal static Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        ExecuteAsync(options, output, error, serverCommandLine: null, cancellationToken);

    /// <summary>
    /// Runs the measurement over a server the caller names, which is how the tests reach it.
    /// </summary>
    /// <param name="options">The parsed command line: the workspace.</param>
    /// <param name="output">Where the table goes.</param>
    /// <param name="error">Where the header and the parity failures go.</param>
    /// <param name="serverCommandLine">
    /// The command line to start as the server, or <see langword="null"/> to start this binary — which is what the
    /// command does when a developer runs it, and what a test cannot do from inside its own host.
    /// </param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>0 when every query answered identically, 1 when one did not, 2 when the run could not be set up.</returns>
    internal static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        string? serverCommandLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!CliHost.TryOpenWorkspace(options, error, out var opened))
        {
            return ExitCodes.Usage;
        }

        var workspace = opened!;
        var queriesPath = Path.Combine(workspace.Root, QueriesRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(queriesPath))
        {
            await error.WriteLineAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"error: mcp bench reads its questions from {QueriesRelativePath}, and {workspace.Root} has no such file.")
                    .AsMemory(),
                cancellationToken).ConfigureAwait(false);

            return ExitCodes.Usage;
        }

        var queries = ReadQueries(await File.ReadAllLinesAsync(queriesPath, cancellationToken).ConfigureAwait(false));
        if (queries.Count == 0)
        {
            await error.WriteLineAsync(
                $"error: {QueriesRelativePath} holds no questions.".AsMemory(),
                cancellationToken).ConfigureAwait(false);

            return ExitCodes.Usage;
        }

        var services = new ServiceCollection();
        services.AddMcpServer(workspace, new McpServerOptions { Refresh = false });

        await using var provider = services.BuildServiceProvider();

        var status = await provider
            .GetRequiredService<ICodeIndex>()
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        // A bench over a missing index would measure the "run `sharpcoreagent index` first" refusal twelve times and
        // call it a result, so it refuses instead.
        if (status.Freshness == IndexFreshness.Missing)
        {
            await error.WriteLineAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"error: mcp bench needs a built index, and {workspace.Root} has none ({status.Files} files). Run 'sharpcoreagent index' first.")
                    .AsMemory(),
                cancellationToken).ConfigureAwait(false);

            return ExitCodes.Usage;
        }

        var tool = provider
            .GetServices<ITool>()
            .Single(candidate => string.Equals(candidate.Definition.Name, SearchCodeTool.Name, StringComparison.Ordinal));

        await error.WriteLineAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"sharpcoreagent mcp bench: {workspace.Root} · index {IndexStatusDocument.State(status.Freshness)} ({status.Files} files, {status.Chunks} passages) · {queries.Count} queries")
                .AsMemory(),
            cancellationToken).ConfigureAwait(false);

        await using var host = new McpToolHost(
            [serverCommandLine ?? ServerCommandLine(workspace.Root)],
            new McpClientOptions());

        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (McpStartupException ex)
        {
            await error.WriteLineAsync(
                $"error: mcp bench could not start its own server: {ex.Message}".AsMemory(),
                cancellationToken).ConfigureAwait(false);

            return ExitCodes.Failed;
        }

        var rows = await MeasureAsync(
            tool,
            host.Servers[0],
            workspace,
            queries,
            options.Verbose,
            error,
            cancellationToken).ConfigureAwait(false);

        var report = new BenchReport(rows, queries.Count);

        await output.WriteAsync(Render(report).AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        return report.IsComplete ? ExitCodes.Success : ExitCodes.Failed;
    }

    /// <summary>
    /// Asks every question twice — once in process and once over stdio — and keeps the row only where the two answers
    /// agree character for character.
    /// </summary>
    /// <param name="tool">The in-process <c>search_code</c>, resolved from the container <c>mcp serve</c> builds.</param>
    /// <param name="server">The running server, spoken to through this product's own client.</param>
    /// <param name="workspace">The workspace the answers cite.</param>
    /// <param name="queries">The questions, in the order the file lists them.</param>
    /// <param name="verbose">Whether to report each query's two durations as it finishes.</param>
    /// <param name="error">Where a query that failed or disagreed is named.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>One row per query that answered identically on both sides.</returns>
    private static async Task<IReadOnlyList<BenchRow>> MeasureAsync(
        ITool tool,
        McpServerConnection server,
        Workspace workspace,
        IReadOnlyList<string> queries,
        bool verbose,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var rows = new List<BenchRow>(queries.Count);

        foreach (var query in queries)
        {
            var arguments = JsonSerializer.Serialize(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["query"] = query });

            using var payload = JsonDocument.Parse(arguments);

            var beforeInProcess = Stopwatch.GetTimestamp();
            var inProcess = await tool
                .ExecuteAsync(
                    new ToolCall(
                        string.Create(CultureInfo.InvariantCulture, $"bench_{rows.Count + 1}"),
                        SearchCodeTool.Name,
                        arguments),
                    new ToolInvocationContext(workspace),
                    cancellationToken)
                .ConfigureAwait(false);
            var inProcessElapsed = Stopwatch.GetElapsedTime(beforeInProcess);

            // Only the stdio call is reported: the question SC-005 asks is what a client waits for, and the in-process
            // half exists to prove the answer it waits for is the tool's own.
            var started = Stopwatch.GetTimestamp();
            var overStdio = await server
                .CallAsync(SearchCodeTool.Name, payload.RootElement, TimeSpan.FromMinutes(2), cancellationToken)
                .ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (verbose)
            {
                // The two numbers side by side are how a slow run is explained: a call that is slow on both sides is
                // the index or the tool, and one that is slow on only one side is the hop.
                await error.WriteLineAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  '{query}' · in-process {inProcessElapsed.TotalMilliseconds:0} ms · stdio {elapsed.TotalMilliseconds:0} ms")
                        .AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }

            if (inProcess.IsError || overStdio.IsError)
            {
                await error.WriteLineAsync(
                    $"  '{query}' could not be answered: {inProcess.Content} {overStdio.Text}".AsMemory(),
                    cancellationToken).ConfigureAwait(false);

                continue;
            }

            if (!string.Equals(inProcess.Content, overStdio.Text, StringComparison.Ordinal))
            {
                // Named rather than counted: a developer reading this needs the query, not a tally.
                await error.WriteLineAsync(
                    $"  '{query}' answered differently in process and over stdio (SC-002)".AsMemory(),
                    cancellationToken).ConfigureAwait(false);

                continue;
            }

            rows.Add(new BenchRow(
                query,
                overStdio.Text.Length,
                CitedChars(workspace.Root, overStdio.Text),
                (int)elapsed.TotalMilliseconds));
        }

        return rows;
    }

    /// <summary>
    /// Reads the questions out of the fixture file.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <returns>The questions, trimmed, with comments and blank lines dropped.</returns>
    internal static IReadOnlyList<string> ReadQueries(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var queries = new List<string>();

        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }

            queries.Add(text);
        }

        return queries;
    }

    /// <summary>
    /// Sums the characters of the distinct files an answer cites (SC-006's denominator).
    /// </summary>
    /// <param name="root">The workspace root the cited paths are relative to.</param>
    /// <param name="answer">The tool's text.</param>
    /// <returns>The characters of each cited file, counted once per query.</returns>
    /// <remarks>
    /// Whole files rather than the cited ranges: "read the files it cites" is what the model would otherwise do, and
    /// quoting one range out of a 300-line file would flatter the ratio.
    /// </remarks>
    internal static long CitedChars(string root, string answer)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(answer);

        var counted = new HashSet<string>(StringComparer.Ordinal);
        var total = 0L;

        foreach (var line in answer.Split('\n'))
        {
            if (CitedPath(line) is not { } path || !counted.Add(path))
            {
                continue;
            }

            try
            {
                total += File.ReadAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A cited file that is gone contributes nothing; the ratio only ever gets less flattering.
            }
        }

        return total;
    }

    /// <summary>
    /// Reads one block heading's file, or <see langword="null"/> when the line is not a heading.
    /// </summary>
    /// <param name="line">One line of a search answer.</param>
    /// <returns>The workspace-relative path, as <c>CodeIndexFormat.BlockHeading</c> wrote it.</returns>
    /// <remarks>
    /// A heading is <c>1) path:start-end · kind · …</c>. It is parsed by field rather than by pattern because the path
    /// is the only field that can contain almost anything, and the two numbers after the last colon are what tell a
    /// heading apart from a quoted line of source that happens to contain one.
    /// </remarks>
    private static string? CitedPath(string line)
    {
        var rank = line.IndexOf(") ", StringComparison.Ordinal);
        if (rank <= 0 || !int.TryParse(line.AsSpan(0, rank), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var field = line[(rank + 2)..];
        var separator = field.IndexOf(" · ", StringComparison.Ordinal);

        var address = separator < 0 ? field : field[..separator];
        var colon = address.LastIndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var range = address[(colon + 1)..];
        var dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 ||
            !int.TryParse(range.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(range.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return address[..colon];
    }

    /// <summary>
    /// Renders the measurement: the three numbers first, then one line per query.
    /// </summary>
    /// <param name="report">The run.</param>
    /// <returns>The table, newline-terminated.</returns>
    /// <remarks>
    /// The summary comes first because the summary is what gets pasted into <c>plan.md</c>; the rows are there so a
    /// number that surprises someone can be traced to the query that produced it.
    /// </remarks>
    internal static string Render(BenchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        builder
            .Append("bench · ").Append(report.Attempted.ToString(CultureInfo.InvariantCulture)).Append(" queries")
            .Append(" · evidence ").Append(Number(report.AnswerChars)).Append(" chars")
            .Append(" · sources ").Append(Number(report.SourceChars)).Append(" chars")
            .Append(" · ratio ").Append(Ratio(report.Ratio))
            .Append(" · p95 ").Append(report.P95Milliseconds.ToString(CultureInfo.InvariantCulture)).Append(" ms")
            .Append(" · parity ").Append(report.Identical.ToString(CultureInfo.InvariantCulture))
            .Append('/').Append(report.Attempted.ToString(CultureInfo.InvariantCulture))
            .Append("\n\n  ")
            .Append("chars".PadLeft(6)).Append("  ")
            .Append("source".PadLeft(8)).Append("  ")
            .Append("ratio".PadLeft(6)).Append("  ")
            .Append("ms".PadLeft(5)).Append("  query\n");

        foreach (var row in report.Rows)
        {
            builder
                .Append("  ").Append(Number(row.AnswerChars).PadLeft(6)).Append("  ")
                .Append(Number(row.SourceChars).PadLeft(8)).Append("  ")
                .Append(Ratio(row.Ratio).PadLeft(6)).Append("  ")
                .Append(row.Milliseconds.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ")
                .Append(row.Query).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the command line this process starts as a server: the snippet <c>mcp config</c> prints, with the
    /// workspace pinned and the refresh off.
    /// </summary>
    /// <param name="root">The workspace to serve.</param>
    /// <returns>A command line <see cref="McpToolHost"/> can start through the platform shell.</returns>
    private static string ServerCommandLine(string root)
    {
        var (command, arguments) = McpClientSnippets.Invocation("mcp", "serve");
        var builder = new StringBuilder(Quote(command));

        // The snippet's arguments already carry `mcp serve`; the two flags after them are what the bench adds, and
        // --no-refresh is deliberate: the harness measures answers, not a walk of the tree.
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder
            .Append(" --workspace ").Append(Quote(root))
            .Append(" --no-refresh")
            .ToString();
    }

    private static string Quote(string value) => string.Concat("\"", value, "\"");

    /// <summary>Renders a count with groups of three separated by a space, as the index command does.</summary>
    /// <param name="value">The count.</param>
    /// <returns>For example <c>1 908</c>.</returns>
    private static string Number(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    private static string Ratio(double ratio) => ratio.ToString("0.000", CultureInfo.InvariantCulture);
}
