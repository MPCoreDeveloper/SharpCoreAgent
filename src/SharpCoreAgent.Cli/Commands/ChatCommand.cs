using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>sharpcoreagent chat</c>: a prompt, a turn, a prompt again — on one transcript.
/// </summary>
/// <remarks>
/// The loop owns no model state: <see cref="AgentSession"/> holds the transcript and the active model,
/// so <c>/model</c> and <c>/approval</c> are session mutations rather than rebuilds of the container.
/// The transcript is written after every turn, which is what makes a crash, a Ctrl+C or a closed
/// terminal cost at most the turn in flight.
/// </remarks>
internal static class ChatCommand
{
    /// <summary>
    /// Runs the interactive loop until the developer leaves.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="scope">The Ctrl+C split between cancelling a turn and leaving.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ExecuteAsync(CliOptions cli, CancellationScope scope)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(scope);

        if (!CliHost.TryOpenWorkspace(cli, Console.Error, out var workspace))
        {
            return ExitCodes.Usage;
        }

        using var provider = CliHost.Build(cli);
        var model = provider.GetRequiredService<ModelDescriptor>();
        var credentials = provider.GetRequiredService<ICredentialStore>();
        if (!CliHost.TryEnsureCredentials(model, credentials, Console.Error, cli.Fallback))
        {
            return ExitCodes.Credentials;
        }

        var store = provider.GetRequiredService<ISessionStore>();
        var options = provider.GetRequiredService<AgentOptions>();
        var agent = provider.GetRequiredService<CodingAgent>();
        var theme = CliHost.ThemeFor(cli);
        var observer = new ConsoleObserver(Console.Out, Console.Error, json: false, cli.Quiet, theme);
        var path = Path.GetFullPath(
            cli.SessionPath ?? CliOptions.DefaultSessionPath(workspace!.Root, DateTimeOffset.UtcNow));

        var (session, failure) = await OpenAsync(cli, workspace!, model, store, credentials, options, path)
            .ConfigureAwait(false);
        if (session is null)
        {
            return failure;
        }

        if (!cli.Quiet)
        {
            if (cli.CommandExplicit)
            {
                await WriteErrorLineAsync(WorkspaceLine(session, theme), scope.ExitToken).ConfigureAwait(false);
            }
            else
            {
                await WriteBannerAsync(session, cli, path, theme, scope.ExitToken).ConfigureAwait(false);
            }
        }

        return await RunLoopAsync(provider, cli, scope, session, store, agent, observer, path, theme).ConfigureAwait(false);
    }

    /// <summary>
    /// Says what this session is, before the first prompt.
    /// </summary>
    /// <param name="session">The conversation, for the model and the workspace.</param>
    /// <param name="cli">The command line, for the approval and agent modes.</param>
    /// <param name="path">The transcript path, already absolute.</param>
    /// <param name="theme">The palette the chrome is rendered with.</param>
    /// <param name="cancellationToken">Cancelled when the developer leaves.</param>
    /// <returns>The pending writes.</returns>
    /// <remarks>
    /// Only a session the developer did not ask for by name gets this: someone who typed <c>chat</c> has
    /// already decided what they wanted, while someone who typed the product name is looking at an
    /// answer they did not request and needs to know what it is, what it costs and how to leave.
    /// </remarks>
    private static async Task WriteBannerAsync(
        AgentSession session,
        CliOptions cli,
        string path,
        AnsiTheme theme,
        CancellationToken cancellationToken)
    {
        await WriteErrorLineAsync(HelpText.VersionLine, cancellationToken).ConfigureAwait(false);
        await WriteErrorLineAsync(
            $"{theme.Dim("model:")} {session.ActiveModel.QualifiedName}  {theme.Dim("approval:")} {Name(cli.Approval)}  {theme.Dim("mode:")} {Name(cli.Mode)}",
            cancellationToken).ConfigureAwait(false);
        await WriteErrorLineAsync(WorkspaceLine(session, theme), cancellationToken).ConfigureAwait(false);

        if (cli.SaveSession)
        {
            await WriteErrorLineAsync($"{theme.Dim("session:")} {path}", cancellationToken).ConfigureAwait(false);
        }

        await WriteErrorLineAsync(
            theme.Dim("Type a request, or /help for the commands; /exit leaves. Ctrl+C cancels a turn."),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders the workspace line both session starts share.
    /// </summary>
    /// <param name="session">The conversation, for the workspace root.</param>
    /// <param name="theme">The palette the label is rendered with.</param>
    /// <returns>The line, without its newline.</returns>
    private static string WorkspaceLine(AgentSession session, AnsiTheme theme) =>
        $"{theme.Dim("workspace:")} {session.Workspace.Root}";

    /// <summary>
    /// Prompts, runs a turn, saves the transcript — until the developer leaves.
    /// </summary>
    /// <param name="provider">The container the turn runs in.</param>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="scope">The Ctrl+C split between cancelling a turn and leaving.</param>
    /// <param name="session">The conversation, mutated in place.</param>
    /// <param name="store">The session store.</param>
    /// <param name="agent">The agent that runs a turn.</param>
    /// <param name="observer">Where the turn's events go.</param>
    /// <param name="path">The transcript path, already absolute.</param>
    /// <param name="theme">The palette the loop's own lines are rendered with.</param>

    /// <returns>The process exit code.</returns>
    private static async Task<int> RunLoopAsync(
        IServiceProvider provider,
        CliOptions cli,
        CancellationScope scope,
        AgentSession session,
        ISessionStore store,
        CodingAgent agent,
        IAgentObserver observer,
        string path,
        AnsiTheme theme)
    {
        AgentReport? last = null;
        while (true)
        {
            var line = await ReadLineAsync(session, scope, theme).ConfigureAwait(false);

            // null is end of input (Ctrl+Z on Windows, Ctrl+D elsewhere) or Ctrl+C at the prompt,
            // which means "leave": there is no turn to abandon.
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '/')
            {
                var (keepGoing, updated) = HandleCommand(line, session, last, cli, path, theme);
                if (!keepGoing)
                {
                    break;
                }

                last = updated;
                continue;
            }

            last = await RunTurnAsync(provider, cli, agent, session, line, observer, scope).ConfigureAwait(false);

            if (cli.SaveSession
                && !await CliHost.TrySaveSessionAsync(store, session, path, Console.Error).ConfigureAwait(false))
            {
                return ExitCodes.SessionIo;
            }
        }

        return await FinishAsync(cli, scope, session, store, path).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the transcript the loop produced and returns the code the conversation ends with.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="scope">Supplies the token the final writes run on.</param>
    /// <param name="session">The conversation to persist.</param>
    /// <param name="store">The session store.</param>
    /// <param name="path">The transcript path, already absolute.</param>
    /// <returns><see cref="ExitCodes.SessionIo"/> when the transcript could not be written.</returns>
    private static async Task<int> FinishAsync(
        CliOptions cli,
        CancellationScope scope,
        AgentSession session,
        ISessionStore store,
        string path)
    {
        if (!cli.SaveSession)
        {
            return ExitCodes.Success;
        }

        if (!await CliHost.TrySaveSessionAsync(store, session, path, Console.Error).ConfigureAwait(false))
        {
            return ExitCodes.SessionIo;
        }

        if (!cli.Quiet)
        {
            await WriteErrorLineAsync($"session: {path}", scope.ExitToken).ConfigureAwait(false);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Paints the prompt and reads one line.
    /// </summary>
    /// <param name="session">The conversation, for the model name in the prompt.</param>
    /// <param name="scope">Cancelled by Ctrl+C, which ends the conversation.</param>
    /// <param name="theme">The palette for the prompt.</param>
    /// <returns>The trimmed line, or <see langword="null"/> when the developer left.</returns>
    private static async Task<string?> ReadLineAsync(AgentSession session, CancellationScope scope, AnsiTheme theme)
    {
        await WriteErrorAsync(
            $"{theme.Dim(session.ActiveModel.QualifiedName)} {theme.Accent("›")} ",
            scope.ExitToken).ConfigureAwait(false);
        await Console.Error.FlushAsync(scope.ExitToken).ConfigureAwait(false);

        try
        {
            return (await Console.In.ReadLineAsync(scope.ExitToken).ConfigureAwait(false))?.Trim();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes one line to standard error, on the conversation's token.
    /// </summary>
    /// <param name="text">The line to write.</param>
    /// <param name="cancellationToken">Cancelled when the developer leaves.</param>
    /// <returns>The pending write.</returns>
    private static Task WriteErrorLineAsync(string text, CancellationToken cancellationToken) =>
        Console.Error.WriteLineAsync(text.AsMemory(), cancellationToken);

    /// <summary>
    /// Writes a prompt to standard error, on the conversation's token.
    /// </summary>
    /// <param name="text">The prompt, without its trailing space.</param>
    /// <param name="cancellationToken">Cancelled when the developer leaves.</param>
    /// <returns>The pending write.</returns>
    private static Task WriteErrorAsync(string text, CancellationToken cancellationToken) =>
        Console.Error.WriteAsync(text.AsMemory(), cancellationToken);

    /// <summary>
    /// Runs one turn inside the cancellation scope's turn split.
    /// </summary>
    /// <param name="provider">The container the turn runs in.</param>
    /// <param name="cli">The parsed command line, for the index budget.</param>
    /// <param name="agent">The agent that runs the turn.</param>
    /// <param name="session">The conversation to append to.</param>
    /// <param name="prompt">What the developer typed.</param>
    /// <param name="observer">Where the turn's events go.</param>
    /// <param name="scope">Supplies the turn's token and restores the exit split afterwards.</param>
    /// <returns>The turn's report.</returns>
    /// <remarks>
    /// The refresh runs here rather than once at startup because a turn can change the workspace: a file written by
    /// <c>write_file</c> in one turn is stale evidence in the next, and this is the seam that notices. It is silent while
    /// nothing changed, so a steady session pays one metadata read per turn and prints nothing.
    /// </remarks>
    private static async Task<AgentReport> RunTurnAsync(
        IServiceProvider provider,
        CliOptions cli,
        CodingAgent agent,
        AgentSession session,
        string prompt,
        IAgentObserver observer,
        CancellationScope scope)
    {
        var turn = scope.BeginTurn();
        try
        {
            await IndexCommand.RefreshAsync(provider, cli, cli.Quiet, Console.Out, turn).ConfigureAwait(false);

            return await agent.RunTurnAsync(session, prompt, observer, turn).ConfigureAwait(false);
        }
        finally
        {
            scope.EndTurn();
        }
    }

    /// <summary>
    /// Opens the conversation: a new session, or the transcript named by <c>--session</c>.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="workspace">The workspace the developer asked for.</param>
    /// <param name="model">The model the command line resolved.</param>
    /// <param name="store">The session store.</param>
    /// <param name="credentials">The credential source, for the resumed model.</param>
    /// <param name="options">The loop's options, for the system prompt.</param>
    /// <param name="path">The transcript path, already absolute.</param>
    /// <returns>The session, or <see langword="null"/> and the exit code that explains why.</returns>
    private static async Task<(AgentSession? Session, int Failure)> OpenAsync(
        CliOptions cli,
        Workspace workspace,
        ModelDescriptor model,
        ISessionStore store,
        ICredentialStore credentials,
        AgentOptions options,
        string path)
    {
        if (cli.SessionPath is null || !File.Exists(path))
        {
            // A named but missing transcript starts a new conversation at the same path, so the first
            // turn writes a file the developer already knows how to find.
            return (new AgentSession(workspace, model, cli.Approval, options.SystemPrompt) { Mode = cli.Mode }, ExitCodes.Success);
        }

        var snapshot = await CliHost.TryLoadSessionAsync(store, path, Console.Error).ConfigureAwait(false);
        if (snapshot is null)
        {
            return (null, ExitCodes.SessionIo);
        }

        try
        {
            // The session file is the authority on which model the conversation was using, not the
            // command line: resuming with a different model would change the meaning of the transcript.
            var resumedModel = ModelCatalog.DescriptorFor(snapshot.Provider, snapshot.ModelId);
            var resumedWorkspace = Directory.Exists(snapshot.WorkspaceRoot)
                ? Workspace.Create(snapshot.WorkspaceRoot)
                : workspace;
            var session = AgentSession.FromSnapshot(snapshot, resumedWorkspace, resumedModel);
            session.Mode = cli.Mode;

            if (!CliHost.TryEnsureCredentials(resumedModel, credentials, Console.Error, cli.Fallback))
            {
                return (null, ExitCodes.Credentials);
            }

            if (!cli.Quiet)
            {
                await Console.Error.WriteLineAsync(
                    $"resumed {path} ({session.Messages.Count} message(s)) on {resumedModel.QualifiedName}").ConfigureAwait(false);
            }

            return (session, ExitCodes.Success);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            await Console.Error.WriteLineAsync(
                $"error: the session at '{path}' names a model or workspace this build cannot open: {ex.Message}").ConfigureAwait(false);
            return (null, ExitCodes.SessionIo);
        }
    }

    /// <summary>
    /// Runs one <c>/command</c>.
    /// </summary>
    /// <param name="line">The trimmed input, starting with <c>/</c>.</param>
    /// <param name="session">The conversation the command may mutate.</param>
    /// <param name="last">The most recent turn's report.</param>
    /// <param name="cli">The parsed command line, for the transcript path and quiet flag.</param>
    /// <param name="path">The absolute transcript path.</param>
    /// <param name="theme">The palette the command's own lines are rendered with.</param>
    /// <returns>Whether to keep reading, and the report <c>/files</c> should describe.</returns>
    private static (bool KeepGoing, AgentReport? Last) HandleCommand(
        string line,
        AgentSession session,
        AgentReport? last,
        CliOptions cli,
        string path,
        AnsiTheme theme)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var command = space < 0 ? line : line[..space];
        var argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();

        if (Is(command, "/exit") || Is(command, "/quit"))
        {
            return (false, last);
        }

        if (Is(command, "/help"))
        {
            Console.Out.WriteLine(HelpText.ChatCommands);
            return (true, last);
        }

        if (Is(command, "/model"))
        {
            SwitchModel(session, argument, theme);
            return (true, last);
        }

        if (Is(command, "/approval"))
        {
            if (CliOptions.TryParseApproval(argument, out var mode))
            {
                session.Approval = mode;
                Console.Error.WriteLine($"{theme.Dim("approval:")} {Name(mode)}");
            }
            else
            {
                Console.Error.WriteLine("usage: /approval readonly|ask|allow");
            }

            return (true, last);
        }

        if (Is(command, "/mode"))
        {
            if (argument.Length == 0)
            {
                Console.Out.WriteLine($"{theme.Dim("mode:")} {Name(session.Mode)}");
            }
            else if (CliOptions.TryParseMode(argument, out var mode))
            {
                session.Mode = mode;
                Console.Error.WriteLine($"{theme.Dim("mode:")} {Name(mode)}");
            }
            else
            {
                Console.Error.WriteLine("usage: /mode plan|act");
            }

            return (true, last);
        }

        if (Is(command, "/compact"))
        {
            var keep = argument.Length == 0 ? 8 : ParseKeep(argument);
            var removed = session.Compact(keep);
            Console.Out.WriteLine(removed == 0
                ? "nothing to compact: the transcript already fits"
                : $"compacted: dropped {removed} old message(s), kept the system prompts and the last {keep}");
            return (true, last);
        }

        if (Is(command, "/usage"))
        {
            PrintUsage(session, theme);
            return (true, last);
        }

        if (Is(command, "/cost"))
        {
            PrintCost(session, theme);
            return (true, last);
        }

        if (Is(command, "/files"))
        {
            PrintFiles(last, theme);
            return (true, last);
        }

        if (Is(command, "/session"))
        {
            Console.Out.WriteLine(cli.SaveSession ? path : "session saving is off (--no-session)");
            return (true, last);
        }

        Console.Error.WriteLine($"unknown command '{command}'. Type /help to see the commands.");
        return (true, last);
    }

    /// <summary>
    /// Applies <c>/model</c>, which takes a bare identifier or a <c>provider:id</c> pair.
    /// </summary>
    /// <param name="session">The conversation to switch.</param>
    /// <param name="argument">The argument, or empty to report the active model.</param>
    /// <param name="theme">The palette the confirmation is rendered with.</param>
    private static void SwitchModel(AgentSession session, string argument, AnsiTheme theme)
    {
        if (argument.Length == 0)
        {
            Console.Out.WriteLine(theme.Strong(session.ActiveModel.QualifiedName));
            return;
        }

        if (ModelCatalog.TryParseQualified(argument, out var qualified))
        {
            Apply(session, qualified!, theme);
            return;
        }

        if (ModelCatalog.TryResolveAlias(argument, out var aliasProvider, out var aliasModel))
        {
            Apply(session, ModelCatalog.DescriptorFor(aliasProvider, aliasModel), theme);
            return;
        }

        if (argument.Contains(':', StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"unknown model '{argument}'. Name a known provider, for example grok:grok-4.7.");
            return;
        }

        // A bare identifier keeps the provider: "/model deepseek-reasoner" while on deepseek.
        try
        {
            Apply(session, ModelCatalog.DescriptorFor(session.ActiveModel.Provider, argument), theme);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    private static void Apply(AgentSession session, ModelDescriptor model, AnsiTheme theme)
    {
        if (!model.SupportsTools)
        {
            // Switching would leave the developer in a conversation where every tool call is refused
            // by the loop itself, so the switch is refused with the reason instead.
            Console.Error.WriteLine(
                $"refused: {model.QualifiedName} is declared as not supporting tool calls, so a turn on it could not use a single tool.");
            return;
        }

        session.SwitchModel(model);
        Console.Out.WriteLine($"{theme.Dim("model:")} {theme.Strong(model.QualifiedName)}");
    }

    private static void PrintUsage(AgentSession session, AnsiTheme theme)
    {
        var usage = session.UsageByModel;
        if (usage.Count == 0)
        {
            Console.Out.WriteLine(theme.Dim("no usage reported yet"));
            return;
        }

        foreach (var row in usage)
        {
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{theme.Strong(row.ModelId)}  {Count(row.InputTokens)} in / {Count(row.OutputTokens)} out  {theme.Dim(string.Create(CultureInfo.InvariantCulture, $"{row.Turns} turn(s)"))}"));
        }

        Console.Out.WriteLine($"{theme.Dim("total")}  {session.TotalUsage}");
    }

    /// <summary>
    /// Prints <c>/cost</c>: what the tokens reported so far would cost at the prices this build knows.
    /// </summary>
    /// <param name="session">The session whose per-model usage is priced.</param>
    /// <param name="theme">The palette the lines are rendered with.</param>
    /// <remarks>
    /// The as-of date is printed before any number, because the number is only as good as the table
    /// behind it. A line the build cannot price, and a line the provider never measured, say so in
    /// words: neither is ever rendered as <c>$0.00</c>, which would read as a measurement. The same
    /// rule applies to the total — it is printed as an amount only when at least one line could be
    /// priced and measured.
    /// </remarks>
    private static void PrintCost(AgentSession session, AnsiTheme theme)
    {
        var report = CostReport.Create(session.UsageByModel);
        if (report.Lines.Count == 0)
        {
            Console.Out.WriteLine(theme.Dim("no usage reported yet, so no cost is known"));
            return;
        }

        Console.Out.WriteLine(theme.Dim(string.Create(
            CultureInfo.InvariantCulture,
            $"prices as of {ModelPricing.AsOf:yyyy-MM-dd}, {ModelPricing.Currency} per million tokens; the peak rate is used where the provider publishes tiers")));

        foreach (var line in report.Lines)
        {
            var tokens = string.Create(
                CultureInfo.InvariantCulture,
                $"{Count(line.Usage.InputTokens)} in / {Count(line.Usage.OutputTokens)} out");

            Console.Out.WriteLine($"{theme.Strong(line.ModelId)}  {tokens}  {Amount(line, theme)}");
        }

        var total = report.Measured == 0
            ? $"{theme.Dim("total")}  {theme.Dim("unknown: no line could be priced and measured")}"
            : $"{theme.Dim("total")}  {Money(report.Total)}";

        if (!report.IsComplete)
        {
            total += theme.Dim(string.Create(
                CultureInfo.InvariantCulture,
                $"  partial: {report.Unpriced} model(s) unpriced, {report.UnknownUsage} without reported usage"));
        }

        Console.Out.WriteLine(total);
    }

    /// <summary>
    /// Renders one report line's money, or the reason there is none.
    /// </summary>
    /// <param name="line">The line to render.</param>
    /// <param name="theme">The palette the reason is rendered with.</param>
    /// <returns>The amount, or a dimmed explanation.</returns>
    private static string Amount(CostLine line, AnsiTheme theme)
    {
        if (!line.IsPriced)
        {
            return theme.Dim("no price known");
        }

        return line.Cost is { } value ? Money(value) : theme.Dim("cost unknown: the provider reported no usage");
    }

    /// <summary>
    /// Formats an amount in the table's currency, at the precision a cheap session needs.
    /// </summary>
    /// <param name="value">The amount.</param>
    /// <returns>Four decimals at most, without a trailing-zero tail.</returns>
    /// <remarks>
    /// A single turn on a small model costs well under a cent, so two decimals would print
    /// <c>$0.00</c> for a session that did cost something — the same lie the unknown paths avoid.
    /// </remarks>
    private static string Money(decimal value) =>
        "$" + value.ToString("0.00####", CultureInfo.InvariantCulture);

    private static void PrintFiles(AgentReport? last, AnsiTheme theme)
    {
        if (last is null)
        {
            Console.Out.WriteLine(theme.Dim("no turn has run yet"));
            return;
        }

        var files = last.FilesTouched;
        var commands = last.CommandsRun;
        if (files.Count == 0 && commands.Count == 0)
        {
            Console.Out.WriteLine(theme.Dim("the last turn changed no files and ran no commands"));
            return;
        }

        foreach (var file in files)
        {
            Console.Out.WriteLine($"{theme.Dim("file   ")} {theme.Path(file)}");
        }

        foreach (var command in commands)
        {
            Console.Out.WriteLine($"{theme.Dim("command")} {command}");
        }
    }

    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static string Name(ApprovalMode mode) => mode switch
    {
        ApprovalMode.ReadOnly => "readonly",
        ApprovalMode.AllowAll => "allow",
        _ => "ask",
    };

    private static string Name(AgentMode mode) => mode == AgentMode.Plan ? "plan" : "act";

    /// <summary>
    /// Reads <c>/compact</c>'s optional window, clamped to a range that keeps a conversation usable.
    /// </summary>
    /// <param name="argument">The text after the command; anything unparsable keeps the default.</param>
    /// <returns>The number of recent messages to keep.</returns>
    private static int ParseKeep(string argument) =>
        int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 1, 200)
            : 8;

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
