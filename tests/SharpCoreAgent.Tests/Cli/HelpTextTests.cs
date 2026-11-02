using System.Globalization;
using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The text a developer reads before running anything, held to the promises the other contracts make.
/// </summary>
/// <remarks>
/// These are cheap assertions with an expensive failure mode: a command that exists but is not in the usage block is
/// a command nobody runs, and an exit code that is not in the table is one scripts learn by crashing into it.
/// </remarks>
public sealed class HelpTextTests
{
    [Fact]
    public void TheVersionLineNamesBothNamesInOrder() =>
        Assert.Matches(@"^Ariadne \d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)? \(SharpCoreAgent\)$", HelpText.VersionLine);

    [Fact]
    public void TheVersionCarriesNoCommitSuffix()
    {
        // SourceLink appends "+<hash>"; a bug report wants the version, so the suffix is cut in ReadVersion.
        Assert.DoesNotContain('+', HelpText.VersionLine);
        Assert.Equal($"Ariadne {HelpText.Version} (SharpCoreAgent)", HelpText.VersionLine);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheIndexCommand()
    {
        Assert.Contains("sharpcoreagent index [options]", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent index --status --json", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsEveryIndexOption()
    {
        string[] options =
        [
            "--status",
            "--rebuild",
            "--verify",
            "--prune",
            "--no-index",
            "--index-budget",
            "--embedder",
            "--tier",
        ];

        Assert.All(options, option => Assert.Contains(option, HelpText.Usage, StringComparison.Ordinal));
    }

    [Fact]
    public void TheChatCommandsDocumentTheCostReport()
    {
        // `/cost` prints a number a developer plans a session on, so the two qualifiers travel with it:
        // the table is dated, and it bills the peak rate where a provider has tiers.
        Assert.Contains("/cost", HelpText.ChatCommands, StringComparison.Ordinal);
        Assert.Contains("peak rate", HelpText.ChatCommands, StringComparison.Ordinal);
        Assert.Contains("dated", HelpText.ChatCommands, StringComparison.Ordinal);
        Assert.Contains("/help", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheFallbackOption()
    {
        // The option only earns its keep if a developer can find it before the 401, so the statuses it
        // covers are part of the row rather than a footnote.
        Assert.Contains("--fallback", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("401", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("5xx", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableDocumentsTheIndexFailure()
    {
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("6 ", StringComparison.Ordinal));

        Assert.Contains("the index", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableRunsFromZeroToSeven()
    {
        for (var code = 0; code <= 7; code++)
        {
            var row = string.Create(CultureInfo.InvariantCulture, $"\n  {code}  ");
            Assert.Contains(row, HelpText.Usage, StringComparison.Ordinal);
        }

        var eight = "\n  8  ";
        Assert.DoesNotContain(eight, HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableDocumentsTheSettingsFailure()
    {
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("7 ", StringComparison.Ordinal));

        Assert.Contains("settings file", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheBareStartAndTheConfigCommand()
    {
        Assert.Contains("sharpcoreagent [options]", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent config set <name> <value>", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("SHARPCOREAGENT_CONFIG", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("config set api-key", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheConfigCommandForClients()
    {
        // Two of the three files are found from the repository, so the shape of the verb has to be in the block a
        // developer reads before running anything — and so does the key Visual Studio reads, which is the one a
        // developer is most likely to be missing when the server does not appear in the IDE.
        Assert.Contains("sharpcoreagent mcp config --client", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--client <name>", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("visualstudio", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--write", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("mcp config  —", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.sharpcoreagent]", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains(".vscode/mcp.json", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServeSectionIsGolden()
    {
        // The whole section, verbatim: a paragraph that drifts from the shipped behaviour is the one kind of help
        // text that is worse than no help text, and comparing it as a block is how a drift gets noticed.
        string[] section =
        [
            "Serve",
            "  sharpcoreagent mcp serve [options]          the same shape the --mcp flag speaks, served",
            "  sharpcoreagent mcp config --client <name>   the block that adds it to claude, vscode, visualstudio or codex",
            "  sharpcoreagent mcp bench                    the twelve fixture questions, asked twice and measured",
            "  Answers the Model Context Protocol on stdin and stdout for one workspace: the five read-only",
            "  evidence tools — search_code, find_symbol, find_references, code_context and index_status —",
            "  and nothing else. No key, no chat model, no network and no tool that writes or starts a",
            "  process is in the process at all.",
            string.Empty,
            "  Before a tool call answers, the index is brought up to date at most once every five seconds",
            "  and never for longer than three. A refresh that cannot run — no index yet, another writer,",
            "  a read-only checkout — is not an error: the call answers from the index that is there and the",
            "  tool's own heading says how fresh it is. --no-refresh skips the walk entirely, for a checkout",
            "  nobody should write to. Nothing here is a setting; both budgets belong to the capability.",
            string.Empty,
            "  stdout carries the protocol and nothing else. The ready line and every diagnostic go to",
            "  stderr, and the index is opened per call and released again, so a server held open for a day",
            "  never stops 'sharpcoreagent index' in another terminal. End of input or Ctrl+C ends it at 0.",
            string.Empty,
            "  Nothing here is a setting and nothing here needs a key, which is why wiring an agent is one",
            "  command: 'mcp config --client claude' prints the block for .mcp.json at the repository root,",
            "  'visualstudio' the one Visual Studio reads from that same .mcp.json, 'vscode' the one for",
            "  .vscode/mcp.json, and 'codex' the [mcp_servers.sharpcoreagent] table for ~/.codex/config.toml.",
            "  Each names this binary's absolute path and 'mcp serve', so the agent serves the directory it",
            "  starts the server in; put --workspace in the args to pin another. The block is the only thing",
            "  on stdout, so a redirect writes the file, and the one line naming that file goes to stderr.",
            string.Empty,
            "  --write merges the block into that file instead of printing it, because a redirect truncates:",
            "  the file keeps every key it had, the client's object keeps every other server, and this entry",
            "  keeps the env and cwd somebody added to it. An entry that already matches is left alone and",
            "  said so. A file that is not JSON, or whose 'servers' is not an object, exits 7 with the file",
            "  untouched. Run it once per client and one committed .mcp.json carries both keys — mcpServers",
            "  for Claude Code, servers for Visual Studio — with neither client losing the other's.",
            string.Empty,
            "  'mcp bench' asks the twelve questions in specs/013-evidence-mcp-server/bench/queries.txt",
            "  twice — in process and over stdio — and prints, for each answer, the characters it spent",
            "  against the characters of the whole files it cites, plus the p95 of the call. --verbose",
            "  adds both durations per query, which is how a slow run is attributed to the hop or to",
            "  the call. It needs a built index and says so otherwise. A query whose two answers differ",
            "  is a failure rather than a row: the bench is where 'the server adds nothing' is a",
            "  number and not a claim.",
        ];

        Assert.Contains(string.Join('\n', section), Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsThePanelCommand()
    {
        // A command that binds a port is the last one that should be discovered from a changelog: the synopsis line, the
        // two routes and the loopback-only sentence are all asserted, because each of them answers a question a
        // developer would otherwise have to answer by reading the source.
        Assert.Contains("sharpcoreagent ui --serve [options]", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--port <n>", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--refresh", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("GET /api/status.json", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("there is no --host", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent ui --serve --port 8080 --refresh", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelSectionIsGolden()
    {
        // The whole section, verbatim, for the reason TheServeSectionIsGolden gives: a paragraph that drifts from the
        // shipped behaviour is worse than no paragraph at all.
        string[] section =
        [
            "Panel",
            "  sharpcoreagent ui --serve [options]         the index as a page, until Ctrl+C",
            "  --serve                  ui          —           serve the panel; there is no other ui action",
            "  --port <n>               ui          0           the port to bind; 0 picks a free one",
            "  --refresh                ui          off         walk the index before each answer, inside --index-budget",
            "  --json                   ui          off         the object naming the URL, then the panel keeps serving",
            string.Empty,
            "  GET / is the page and GET /api/status.json is the object 'index --status --json' prints — the same",
            "  bytes, built by the same code — and neither is cached, because a cached page would show numbers",
            "  that are no longer the index's. Nothing else is answered: any other path, any method but GET, and",
            "  any Host header that is not 127.0.0.1:<port> gets 404 or 403 with an empty body, because a",
            "  loopback listener that answers any Host is reachable from a hostile page by DNS rebinding.",
            string.Empty,
            "  It reads the index and never writes it. A page load does not walk the tree: that is what --refresh",
            "  adds, bounded by --index-budget, so the safe behaviour is the one that happens. The prefix is",
            "  127.0.0.1 and there is no --host — the page carries this repository's path and counts, and nothing",
            "  here is published to a network. Opening the URL is your action: VS Code's Simple Browser and a",
            "  Visual Studio browser window both render it. Ctrl+C ends the panel at 0; a port nothing can bind",
            "  ends it at 2, with the port named and --port as the fix.",
        ];

        Assert.Contains(string.Join('\n', section), Normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableSaysWhatZeroMeansForThePanel()
    {
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.StartsWith("  0  ", StringComparison.Ordinal));

        Assert.Contains("mcp serve", row, StringComparison.Ordinal);
        Assert.Contains("panel", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheBenchCommand()
    {
        // The measurement is only reproducible if a reader can find the command that produces it, and the one thing
        // it insists on — a built index — is said in the same breath.
        Assert.Contains("sharpcoreagent mcp bench [options]", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("mcp bench", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("ratio", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("p95", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOptionsRowKeepsItsThreeColumns()
    {
        // Flag at 2, the commands it applies to at 27, the default at 39 — the raw string literal in HelpText strips
        // the source indentation, so these are the columns a reader's terminal shows. Rows differ in whether they
        // carry a sentence (`--max-iterations` needs none, its value is the whole answer), so the three cells every
        // row has are what this pins.
        var rows = HelpText.Usage
            .Split('\n')
            .Where(line => line.StartsWith("  --", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(rows);
        Assert.All(
            rows,
            row =>
            {
                Assert.Equal(2, row.IndexOf("--", StringComparison.Ordinal));
                Assert.True(row.Length > 39, $"'{row}' is too short to reach the default column.");
                Assert.NotEqual(' ', row[27]);
                Assert.NotEqual(' ', row[39]);
            });
    }

    [Fact]
    public void TheClientRowReachesTheDescriptionColumn()
    {
        // The row that names the clients, in all four columns: the flag, the command it belongs to, the dash that says
        // it has no default, and the sentence naming the four.
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.StartsWith("  --client <name>", StringComparison.Ordinal));

        Assert.Equal(27, row.IndexOf("mcp config", StringComparison.Ordinal));
        Assert.Equal(39, row.IndexOf('—'));
        Assert.Equal(51, row.IndexOf("claude, vscode, visualstudio or codex", StringComparison.Ordinal));
    }

    [Fact]
    public void TheUsageBlockDocumentsTheServeCommand()
    {
        // The command an editor starts rather than a developer: a reader has to be able to find its shape, and the
        // one flag of its own, without reading the source.
        Assert.Contains("sharpcoreagent mcp serve", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--no-refresh", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("mcp serve   off", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("search_code", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableSaysWhatZeroMeansForAServer()
    {
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("0 ", StringComparison.Ordinal));

        Assert.Contains("mcp serve", row, StringComparison.Ordinal);
        Assert.Contains("end of input", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheCredentialFile()
    {
        // The option exists to remove a restart, so both halves are named: the flag a script passes and
        // the variable a developer sets once. Exit code 3 is where a bad file is reported, so its row
        // says so rather than leaving scripts to discover it.
        Assert.Contains("--credential-file", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("SHARPCOREAGENT_CREDENTIAL_FILE", HelpText.Usage, StringComparison.Ordinal);

        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("3 ", StringComparison.Ordinal));

        Assert.Contains("credential file", row, StringComparison.Ordinal);
    }

    /// <summary>Gets the usage block with one kind of line ending, so a comparison does not depend on the checkout.</summary>
    private static string Normalized => HelpText.Usage.Replace("\r\n", "\n", StringComparison.Ordinal);
}
