using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Configuration;
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
    /// <param name="settingsPath">
    /// The settings file the process resolved at startup, for <c>/config</c>; null resolves it again.
    /// </param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ExecuteAsync(CliOptions cli, CancellationScope scope, string? settingsPath = null)
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

        var path = Path.GetFullPath(
            cli.SessionPath ?? CliOptions.DefaultSessionPath(workspace!.Root, DateTimeOffset.UtcNow));

        // `/config` writes the file `config set` writes, so the path the process resolved is threaded
        // through rather than guessed a second time from the environment.
        var settingsFile = settingsPath ?? AgentSettingsFile.PathFromEnvironment();

        var (session, failure) = await OpenAsync(cli, workspace!, model, store, credentials, options, path)
            .ConfigureAwait(false);
        if (session is null)
        {
            return failure;
        }

        // The budget belongs to the conversation, not to the process. A resumed transcript has already paid for the
        // tokens recorded in it, so it starts with that spend as headroom; from then on `/budget` is the way up.
        session.MaxTotalTokens ??= options.MaxTotalTokens + session.TokensSpent;

        var observer = new ConsoleObserver(
            Console.Out,
            Console.Error,
            json: false,
            cli.Quiet,
            theme,
            verbose: cli.Verbose,
            budget: () => (session.TokensSpent, session.MaxTotalTokens ?? options.MaxTotalTokens));

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

        return await RunLoopAsync(provider, cli, scope, session, store, agent, observer, path, theme, settingsFile).ConfigureAwait(false);
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
        var rows = new List<(string Label, string Value)>
        {
            (string.Empty, HelpText.VersionLine),
            ("workspace", session.Workspace.Root),
            ("model", session.ActiveModel.QualifiedName),
            ("approval", string.Create(
                CultureInfo.InvariantCulture,
                $"{Name(cli.Approval)} · mode {Name(cli.Mode)} · budget {BudgetText(session.MaxTotalTokens)}")),
        };

        // A row only when something is attached, so a session without attachments prints the same banner it
        // always did. The count and the first root answer "may I read somewhere else, and where?" without
        // spending a line per attachment; /roots and list_roots name them all.
        if (session.Workspace.HasAttachments)
        {
            // The path is shortened here rather than by the row renderer, which drops the head of a long value:
            // the head of this one is the count, and a count nobody can read is not the answer FR-011 asks for.
            rows.Insert(2, ("roots", string.Create(
                CultureInfo.InvariantCulture,
                $"+{session.Workspace.Attachments.Count} read-only · {Shorten(session.Workspace.Attachments[0].Root)}")));
        }

        if (cli.SaveSession)
        {
            rows.Add(("session", path));
        }

        // A box that names what this session is, the way a coding CLI greets a developer: the four facts that decide
        // what a turn may do, then one dim line with the ways out. Width is capped so a deep checkout path does not
        // push the borders off the screen, and the border is the only painted part, so the padding stays aligned.
        var width = Math.Clamp(rows.Max(row => Render(row.Label, row.Value, int.MaxValue).Length), 40, 74);
        await WriteErrorLineAsync(theme.Accent("╭" + new string('─', width + 2) + "╮"), cancellationToken).ConfigureAwait(false);
        foreach (var (label, value) in rows)
        {
            await WriteErrorLineAsync(
                $"{theme.Accent("│")} {Render(label, value, width).PadRight(width)} {theme.Accent("│")}",
                cancellationToken).ConfigureAwait(false);
        }

        await WriteErrorLineAsync(theme.Accent("╰" + new string('─', width + 2) + "╯"), cancellationToken).ConfigureAwait(false);
        await WriteErrorLineAsync(
            theme.Dim("/help commands · /budget raise the session budget · Ctrl+C cancels a turn · /exit leaves"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shortens a path from its head so the value of a banner row keeps a readable length.
    /// </summary>
    /// <param name="path">The path to shorten.</param>
    /// <returns>The path, or its tail behind an ellipsis when it is longer than <see cref="BannerPathLength"/>.</returns>
    /// <remarks>
    /// The end of a path is the part that tells two checkouts apart, which is why the head goes. The budget is
    /// small enough that the count in front of the path always survives the row renderer's own shortening.
    /// </remarks>
    private static string Shorten(string path) =>
        path.Length <= BannerPathLength ? path : "…" + path[^(BannerPathLength - 1)..];

    /// <summary>
    /// Lays one banner row out: the label, then the value, shortened from its head when it will not fit.
    /// </summary>
    /// <param name="label">The name of the fact, or empty for a row that has none.</param>
    /// <param name="value">The fact itself.</param>
    /// <param name="width">The interior width of the box.</param>
    /// <returns>The row, never longer than <paramref name="width"/>.</returns>
    /// <remarks>
    /// The label is what makes the row readable, so it is never cut; the value loses its head because the end of a
    /// path is the part that tells two checkouts apart, and the end of the approval row is the budget.
    /// </remarks>
    private static string Render(string label, string value, int width)
    {
        if (label.Length == 0)
        {
            return value.Length <= width ? value : "…" + value[^(width - 1)..];
        }

        var head = label.PadRight(LabelWidth) + "  ";
        var room = width - head.Length;
        return head + (value.Length <= room ? value : "…" + value[^(room - 1)..]);
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
    /// <param name="settingsPath">The settings file <c>/config</c> reads and writes.</param>
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
        AnsiTheme theme,
        string settingsPath)
    {
        AgentReport? last = null;
        var warned = false;
        var paused = false;
        while (true)
        {
            var line = await ReadLineAsync(session, scope, theme, paused).ConfigureAwait(false);

            // null is end of input (Ctrl+Z on Windows, Ctrl+D elsewhere) or Ctrl+C at the prompt,
            // which means "leave": there is no turn to abandon.
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                // A bare Enter after a pause is the shortest way back into the work: the request that ran out of budget
                // is already the last message in the transcript, so nothing has to be retyped.
                if (!paused)
                {
                    continue;
                }

                line = ContinuePrompt;
            }

            if (line[0] == '/' && !Is(line, "/continue"))
            {
                var (keepGoing, updated) = HandleCommand(line, session, last, cli, path, theme, settingsPath);
                if (!keepGoing)
                {
                    break;
                }

                last = updated;
                continue;
            }

            // `/continue` is the same door as the empty line: it is the command a developer reaches for after reading
            // the warning, and it works whether the last request stopped for budget or simply finished its answer.
            var prompt = line[0] == '/' ? ContinuePrompt : line;
            paused = false;
            while (true)
            {
                last = await RunTurnAsync(provider, cli, agent, session, prompt, observer, scope).ConfigureAwait(false);

                if (cli.SaveSession
                    && !await CliHost.TrySaveSessionAsync(store, session, path, Console.Error).ConfigureAwait(false))
                {
                    return ExitCodes.SessionIo;
                }

                if (!IsSessionBudgetSpent(last))
                {
                    warned = await WarnWhenCloseAsync(session, cli, theme, scope, warned).ConfigureAwait(false);
                    if (last.Outcome == AgentOutcome.BudgetExhausted)
                    {
                        await ExplainOtherBudgetAsync(last, theme, scope).ConfigureAwait(false);
                    }

                    break;
                }

                switch (await TryContinueAfterBudgetAsync(session, cli, theme, scope).ConfigureAwait(false))
                {
                    case BudgetAnswer.Resumed:
                        warned = false;
                        prompt = ContinuePrompt;
                        continue;

                    case BudgetAnswer.Paused:
                        // The session stays open. What used to be an exit is now a stop, because the work is still in the
                        // transcript and the developer's next keystroke should be able to pick it up.
                        paused = true;
                        break;

                    default:
                        return ExitCodes.Budget;
                }

                break;
            }
        }

        var exit = await FinishAsync(cli, scope, session, store, path).ConfigureAwait(false);

        // Leaving with a request still paused is not a clean end of the conversation, and a script that checks the exit
        // code has to be able to tell: the transcript holds unfinished work rather than a finished answer.
        return paused && exit == ExitCodes.Success ? ExitCodes.Budget : exit;
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
    /// <param name="paused">Whether the last request stopped for budget and is waiting to be carried on.</param>
    /// <returns>The trimmed line, or <see langword="null"/> when the developer left.</returns>
    /// <remarks>
    /// The paused marker is what makes the stop visible at the cursor rather than three screenfuls up: a developer who
    /// scrolled past the refusal would otherwise read the ordinary prompt as "that request finished".
    /// </remarks>
    private static async Task<string?> ReadLineAsync(
        AgentSession session,
        CancellationScope scope,
        AnsiTheme theme,
        bool paused)
    {
        await WriteErrorAsync(
            string.Concat(
                theme.Dim(session.ActiveModel.Id),
                theme.Dim(" · "),
                theme.Dim(Name(session.Mode)),
                paused ? theme.Warning(" · paused") : string.Empty,
                theme.Accent(" ❯ ")),
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
                ? Workspace.Create(snapshot.WorkspaceRoot).WithAttachments(cli.AdditionalRoots)
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
    /// <param name="settingsPath">The settings file <c>/config</c> reads and writes.</param>
    /// <returns>Whether to keep reading, and the report <c>/files</c> should describe.</returns>
    private static (bool KeepGoing, AgentReport? Last) HandleCommand(
        string line,
        AgentSession session,
        AgentReport? last,
        CliOptions cli,
        string path,
        AnsiTheme theme,
        string settingsPath)
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
            if (argument.Length == 0)
            {
                // Bare `/approval` reports, exactly as `/model` and `/mode` do: a developer who wants to
                // know what the session is set to should not have to guess or restart the loop.
                Console.Out.WriteLine($"{theme.Dim("approval:")} {Name(session.Approval)}{Describe(session.Approval, theme)}");

                // The tools waved through one at a time are part of "what is this session set to", and they are invisible
                // everywhere else: the answer to "why did that not ask me?" has to be findable.
                if (session.ApprovedTools.Count > 0)
                {
                    Console.Out.WriteLine(
                        $"{theme.Dim("approved")} {string.Join(", ", session.ApprovedTools)} {theme.Dim("(this session only; /approval forget revokes)")}");
                }

                return (true, last);
            }

            if (Is(argument, "forget") || Is(argument, "reset"))
            {
                // Revoking is the other half of a session-scoped yes: the developer who typed `t` for a tool they have now
                // finished with should be able to make it ask again without restarting the conversation.
                var forgotten = session.ForgetApprovedTools();
                Console.Out.WriteLine(forgotten == 0
                    ? "approval: nothing to forget; no tool was approved on its own"
                    : $"approval: {forgotten} tool(s) ask again");
                return (true, last);
            }

            if (CliOptions.TryParseApproval(argument, out var mode))
            {
                session.Approval = mode;
                Console.Error.WriteLine($"{theme.Dim("approval:")} {Name(mode)}{Describe(mode, theme)}");
            }
            else
            {
                Console.Error.WriteLine("usage: /approval readonly|ask|allow|forget");
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

        if (Is(command, "/budget"))
        {
            Budget(argument, session, cli, theme);
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

        if (Is(command, "/roots"))
        {
            // The same formatter list_roots uses, so the answer a developer reads and the answer the model reads
            // cannot drift apart. Native separators here: this line is for the shell, not for a tool call.
            Console.Out.WriteLine(RootListing.DescribeForConsole(session.Workspace));
            return (true, last);
        }

        if (Is(command, "/config"))
        {
            Configure(argument, session, cli, settingsPath, theme);
            return (true, last);
        }

        Console.Error.WriteLine($"unknown command '{command}'. Type /help to see the commands.");
        return (true, last);
    }

    /// <summary>
    /// Runs <c>/config</c>: the wizard, or one of its three verbs.
    /// </summary>
    /// <param name="argument">The text after the command; empty asks the questions.</param>
    /// <param name="session">The conversation the change applies to, mid-flight.</param>
    /// <param name="cli">The command line, which supplies everything the session has not overridden.</param>
    /// <param name="settingsPath">The settings file to read and write.</param>
    /// <param name="theme">The palette the confirmations are rendered with.</param>
    /// <remarks>
    /// A change made here is written to the same document <c>config set</c> writes and applied to the live
    /// session, because the two halves of "persist" and "take effect" are only useful together: a developer
    /// who fixes an approval mode in the middle of a conversation expects the next tool call to honour it.
    /// </remarks>
    private static void Configure(
        string argument,
        AgentSession session,
        CliOptions cli,
        string settingsPath,
        AnsiTheme theme)
    {
        var space = argument.IndexOf(' ', StringComparison.Ordinal);
        var verb = space < 0 ? argument : argument[..space];
        var rest = space < 0 ? string.Empty : argument[(space + 1)..].Trim();

        if (verb.Length == 0)
        {
            if (!Terminal.Interactive())
            {
                // The loop can be driven by a script even though its own prompt exists; a wizard cannot.
                Console.Error.WriteLine(
                    "the wizard needs a terminal; use '/config set <name> <value>' here, or 'sharpcoreagent setup' from a shell.");
                return;
            }

            var applied = SetupWizard.Run(Console.In, Console.Out, settingsPath, View(cli, session), out var written);
            if (written > 0)
            {
                ApplyToSession(session, applied, theme);
            }

            return;
        }

        if (Is(verb, "path"))
        {
            Console.Out.WriteLine(settingsPath);
            return;
        }

        if (Is(verb, "show"))
        {
            _ = ConfigCommand.Execute(
                cli with { Command = CliCommand.Config, ConfigAction = ConfigAction.Show },
                settingsPath,
                Environment.GetEnvironmentVariable,
                Console.Out);
            return;
        }

        if (Is(verb, "set"))
        {
            var split = rest.IndexOf(' ', StringComparison.Ordinal);
            if (split < 0)
            {
                Console.Error.WriteLine("usage: /config set <name> <value>");
                return;
            }

            var value = rest[(split + 1)..].Trim();
            try
            {
                var name = CliOptions.EnsureSettable(rest[..split]);
                var applied = CliOptions.WithSetting(View(cli, session), name, value);
                Persist(settingsPath, name, value);
                ApplyToSession(session, applied, theme);
                Console.Out.WriteLine($"{theme.Dim("config:")} {name} = {value} {theme.Dim($"(saved to {settingsPath})")}");
            }
            catch (CliUsageException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
            }

            return;
        }

        Console.Error.WriteLine("usage: /config [show|path|set <name> <value>]; bare /config asks the questions");
    }

    /// <summary>
    /// Projects the live session onto the options a setting is validated against.
    /// </summary>
    /// <param name="cli">The command line, for the values a session does not own.</param>
    /// <param name="session">The conversation, for the values it does.</param>
    /// <returns>Options whose model, approval and mode are what the session is using right now.</returns>
    /// <remarks>
    /// Without this the wizard would show the approval mode the process started with after <c>/approval</c>
    /// or an "allow every tool" answer had already changed it, and the developer would be asked to confirm a
    /// value the session had stopped using.
    /// </remarks>
    private static CliOptions View(CliOptions cli, AgentSession session) => cli with
    {
        Provider = session.ActiveModel.Provider,
        Model = session.ActiveModel.Id,
        Approval = session.Approval,
        Mode = session.Mode,
    };

    /// <summary>
    /// Writes one setting over the document that is already there.
    /// </summary>
    /// <param name="path">The settings file.</param>
    /// <param name="name">The canonical setting name.</param>
    /// <param name="value">The value, already validated.</param>
    /// <exception cref="SharpCoreAgent.Domain.Errors.ConfigurationFileException">The file cannot be read or written.</exception>
    private static void Persist(string path, string name, string value)
    {
        var document = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (existingName, existingValue) in AgentSettingsFile.Read(path))
        {
            document[existingName] = existingValue;
        }

        document[name] = value;
        AgentSettingsFile.Save(path, document);
    }

    /// <summary>
    /// Applies the settings that describe a running conversation.
    /// </summary>
    /// <param name="session">The conversation to change.</param>
    /// <param name="applied">The options the new settings produced.</param>
    /// <param name="theme">The palette the model switch is confirmed with.</param>
    private static void ApplyToSession(AgentSession session, CliOptions applied, AnsiTheme theme)
    {
        session.Approval = applied.Approval;
        session.Mode = applied.Mode;

        ModelDescriptor model;
        try
        {
            model = ModelCatalog.DescriptorFor(applied.Provider, applied.Model);
        }
        catch (ArgumentException ex)
        {
            // A model identifier the build cannot resolve is reported where it was typed. The setting is
            // already saved, so the next start will refuse it too -- but this session keeps the model it
            // has instead of dying mid-conversation.
            Console.Error.WriteLine($"error: {ex.Message}");
            return;
        }

        if (!string.Equals(model.QualifiedName, session.ActiveModel.QualifiedName, StringComparison.Ordinal))
        {
            Apply(session, model, theme);
        }
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
    /// Explains what an approval mode means for the next tool call.
    /// </summary>
    /// <param name="mode">The mode to describe.</param>
    /// <param name="theme">The palette the note is rendered with.</param>
    /// <returns>A parenthesised note, or an empty string when the name says it all.</returns>
    /// <remarks>
    /// <c>allow</c> is the only mode with a scope worth stating: it lasts until the process exits and is
    /// deliberately never written to the settings file, so the note says both things where the developer
    /// chose it rather than burying them in the documentation.
    /// </remarks>
    private static string Describe(ApprovalMode mode, AnsiTheme theme) => mode switch
    {
        ApprovalMode.AllowAll => theme.Dim(" (until this session exits; not saved)"),
        ApprovalMode.ReadOnly => theme.Dim(" (read-only tools only)"),
        _ => theme.Dim(" (destructive tools ask first)"),
    };

    /// <summary>The message sent after a continued budget, so the interrupted request carries on.</summary>
    private const string ContinuePrompt = "Continue from where you stopped.";

    /// <summary>The column every banner label is padded to, so each value starts in the same place.</summary>
    private const int LabelWidth = 9;

    /// <summary>
    /// How much of an attachment path the banner shows before it keeps only the tail.
    /// </summary>
    /// <remarks>
    /// Chosen so <c>+N read-only · </c> plus the path still fits the widest box (74 columns minus the label
    /// column), which is what keeps the count visible for any checkout depth.
    /// </remarks>
    private const int BannerPathLength = 46;

    /// <summary>The smallest ceiling <c>/budget</c> accepts, matching the floor on <c>--max-total-tokens</c>.</summary>
    private const long MinimumCeiling = 1_000;

    /// <summary>
    /// Answers whether a turn ended because the session's token budget ran out.
    /// </summary>
    /// <param name="report">The turn's report.</param>
    /// <returns><see langword="true"/> when more tokens would let the same request continue.</returns>
    /// <remarks>
    /// The outcome alone is not enough: an iteration budget and a wall-clock timeout report the same
    /// <see cref="AgentOutcome.BudgetExhausted"/>, and offering tokens for those would loop forever. The detail is the
    /// only place the loop names which budget it spent, so it is read here and nowhere else.
    /// </remarks>
    private static bool IsSessionBudgetSpent(AgentReport report) =>
        report.Outcome == AgentOutcome.BudgetExhausted
        && report.Detail?.Contains("session token budget", StringComparison.Ordinal) == true;

    /// <summary>
    /// Warns once when the session is close to spending its budget.
    /// </summary>
    /// <param name="session">The conversation, for its spend and ceiling.</param>
    /// <param name="cli">The command line, for the ceiling a session without one falls back to.</param>
    /// <param name="theme">The palette the warning is rendered with.</param>
    /// <param name="scope">Supplies the token the line is written on.</param>
    /// <param name="warned">Whether a warning has already been printed since the last raise.</param>
    /// <returns>Whether a warning has now been printed.</returns>
    /// <remarks>
    /// A budget a developer cannot see is a budget that surprises them, which is exactly how "the session stopped for
    /// no reason" happens. One line at four fifths, with the command that raises it, is the cheapest fix.
    /// </remarks>
    private static async Task<bool> WarnWhenCloseAsync(
        AgentSession session,
        CliOptions cli,
        AnsiTheme theme,
        CancellationScope scope,
        bool warned)
    {
        if (warned || cli.Quiet)
        {
            return warned;
        }

        var ceiling = session.MaxTotalTokens ?? cli.MaxTotalTokens;
        if (ceiling <= 0 || ceiling == long.MaxValue)
        {
            return warned;
        }

        var fraction = (double)session.TokensSpent / ceiling;
        if (fraction < 0.8)
        {
            return warned;
        }

        // Past the ceiling the warning would read as a rounding note, and the honest message is that the next request
        // will be refused — the answer in hand is still worth printing, which is why the turn was allowed to finish.
        var message = session.TokensSpent >= ceiling
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"! the session budget is spent ({session.TokensSpent:n0} of {ceiling:n0} tokens); the next request needs '/budget +{cli.MaxTotalTokens:n0}'")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"! {(int)(fraction * 100)}% of the session budget is spent ({session.TokensSpent:n0} of {ceiling:n0} tokens); '/budget +{cli.MaxTotalTokens:n0}' raises it");

        await WriteErrorLineAsync(theme.Warning(message), scope.ExitToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Says which budget ended a turn that more tokens cannot rescue.
    /// </summary>
    /// <param name="report">The turn's report.</param>
    /// <param name="theme">The palette the line is rendered with.</param>
    /// <param name="scope">Supplies the token the line is written on.</param>
    /// <returns>The pending write.</returns>
    private static Task ExplainOtherBudgetAsync(AgentReport report, AnsiTheme theme, CancellationScope scope) =>
        WriteErrorLineAsync(
            theme.Dim(string.Create(
                CultureInfo.InvariantCulture,
                $"/budget does not apply here: {report.Detail} Try a smaller request, or /mode plan to split it up.")),
            scope.ExitToken);

    /// <summary>
    /// Offers more budget when a session spent the one it had.
    /// </summary>
    /// <param name="session">The conversation, whose ceiling is raised on a yes.</param>
    /// <param name="cli">The command line, for the amount to add.</param>
    /// <param name="theme">The palette the question is rendered with.</param>
    /// <param name="scope">Supplies the token the question is written on.</param>
    /// <returns>Whether to replay the request, keep the session open with it paused, or leave.</returns>
    /// <remarks>
    /// Enter answers yes: a developer whose request was interrupted mid-work meant to finish it, and the guard exists
    /// to stop a runaway spend rather than to make them retype. A no no longer ends the conversation — it pauses the
    /// request, because the same developer may simply want to raise the ceiling on their own terms with <c>/budget</c>,
    /// and an exit takes that decision away. A loop nobody can answer still leaves with the budget code, so a script
    /// reads the same thing it always did.
    /// </remarks>
    private static async Task<BudgetAnswer> TryContinueAfterBudgetAsync(
        AgentSession session,
        CliOptions cli,
        AnsiTheme theme,
        CancellationScope scope)
    {
        var step = Math.Max(cli.MaxTotalTokens, MinimumCeiling);

        if (!Terminal.Interactive())
        {
            await WriteErrorLineAsync(
                theme.Dim("raise it with --max-total-tokens, or start a new session"),
                scope.ExitToken).ConfigureAwait(false);
            return BudgetAnswer.Stop;
        }

        await WriteErrorAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{theme.Warning("!")} Continue with {step:n0} more tokens? [Y/n] "),
            scope.ExitToken).ConfigureAwait(false);
        await Console.Error.FlushAsync(scope.ExitToken).ConfigureAwait(false);

        string? answer;
        try
        {
            answer = await Console.In.ReadLineAsync(scope.ExitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return BudgetAnswer.Stop;
        }

        if (answer is null)
        {
            return BudgetAnswer.Stop;
        }

        var trimmed = answer.Trim();
        if (trimmed.Length > 0 && !Is(trimmed, "y") && !Is(trimmed, "yes"))
        {
            // The hint names both doors: `/continue` for the same request, `/budget` for a ceiling the developer picks,
            // and neither is an exit.
            await WriteErrorLineAsync(
                theme.Dim("paused: the request is the last message in the transcript — /continue carries on, /budget raises the ceiling"),
                scope.ExitToken).ConfigureAwait(false);
            return BudgetAnswer.Paused;
        }

        var raised = session.RaiseBudget(step, cli.MaxTotalTokens);
        await WriteErrorLineAsync(
            theme.Dim(string.Create(CultureInfo.InvariantCulture, $"budget: raised to {raised:n0} tokens")),
            scope.ExitToken).ConfigureAwait(false);

        return BudgetAnswer.Resumed;
    }

    /// <summary>What the loop does with a request that spent the session's budget.</summary>
    private enum BudgetAnswer
    {
        /// <summary>Room was made; the same request runs again.</summary>
        Resumed,

        /// <summary>The developer said no; the request waits for <c>/continue</c> instead of ending the session.</summary>
        Paused,

        /// <summary>Nobody can answer, or the input closed; leave with <see cref="ExitCodes.Budget"/>.</summary>
        Stop,
    }

    /// <summary>
    /// Runs <c>/budget</c>: reports the ceiling, or replaces or raises it for this session.
    /// </summary>
    /// <param name="argument">The text after the command; empty reports.</param>
    /// <param name="session">The conversation the ceiling belongs to.</param>
    /// <param name="cli">The command line, for the default ceiling and the step to raise by.</param>
    /// <param name="theme">The palette the confirmation is rendered with.</param>
    /// <remarks>
    /// The value is not written to the settings file: a raised budget is a decision about this conversation, and
    /// persisting it would quietly authorise the next one too.
    /// </remarks>
    private static void Budget(string argument, AgentSession session, CliOptions cli, AnsiTheme theme)
    {
        if (argument.Length == 0)
        {
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{theme.Dim("budget:")} {BudgetText(session.MaxTotalTokens ?? cli.MaxTotalTokens)} ({session.TokensSpent:n0} spent)"));
            return;
        }

        if (Is(argument, "unlimited"))
        {
            session.MaxTotalTokens = long.MaxValue;
            Console.Out.WriteLine($"{theme.Dim("budget:")} unlimited for this session");
            return;
        }

        var raising = argument[0] == '+';
        var digits = raising ? argument[1..].Trim() : argument;
        if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            if (raising)
            {
                var raised = session.RaiseBudget(value, cli.MaxTotalTokens);
                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{theme.Dim("budget:")} {BudgetText(raised)}"));
                return;
            }

            if (value < MinimumCeiling)
            {
                // The same floor the command line puts on --max-total-tokens: a ceiling below the cost of one request
                // is a session that cannot answer anything, which reads as a bug rather than a limit.
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"budget: {value:n0} tokens would not pay for one request; the smallest ceiling is {MinimumCeiling:n0}"));
                return;
            }

            session.MaxTotalTokens = value;
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{theme.Dim("budget:")} {BudgetText(value)}"));

            if (value <= session.TokensSpent)
            {
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"warning: {session.TokensSpent:n0} tokens are already spent, so the next turn stops as soon as it reports usage."));
            }

            return;
        }

        Console.Error.WriteLine("usage: /budget [<tokens>|+<tokens>|unlimited]");
    }

    /// <summary>
    /// Renders a ceiling the way the banner, the summary and <c>/budget</c> all say it.
    /// </summary>
    /// <param name="ceiling">The ceiling, or null when the session has none of its own.</param>
    /// <returns>The words for it.</returns>
    private static string BudgetText(long? ceiling) => ceiling switch
    {
        null => "default",
        long.MaxValue => "unlimited",
        var limit => string.Create(CultureInfo.InvariantCulture, $"{limit:n0} tokens"),
    };

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
