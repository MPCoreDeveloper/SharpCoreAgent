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
        if (!CliHost.TryEnsureCredentials(model, credentials, Console.Error))
        {
            return ExitCodes.Credentials;
        }

        var store = provider.GetRequiredService<ISessionStore>();
        var options = provider.GetRequiredService<AgentOptions>();
        var agent = provider.GetRequiredService<CodingAgent>();
        var observer = new ConsoleObserver(Console.Out, Console.Error, json: false, cli.Quiet);
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
            Console.Error.WriteLine($"workspace: {session.Workspace.Root}");
        }

        AgentReport? last = null;
        while (true)
        {
            Console.Error.Write($"{session.ActiveModel.QualifiedName} › ");
            Console.Error.Flush();

            string? input;
            try
            {
                input = await Console.In.ReadLineAsync(scope.ExitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C at the prompt means "leave": there is no turn to abandon.
                break;
            }

            if (input is null)
            {
                // End of input (Ctrl+Z on Windows, Ctrl+D elsewhere) ends the conversation too.
                break;
            }

            var line = input.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '/')
            {
                var (keepGoing, updated) = HandleCommand(line, session, last, cli, path);
                if (!keepGoing)
                {
                    break;
                }

                last = updated;
                continue;
            }

            var turn = scope.BeginTurn();
            try
            {
                last = await agent.RunTurnAsync(session, line, observer, turn).ConfigureAwait(false);
            }
            finally
            {
                scope.EndTurn();
            }

            if (cli.SaveSession
                && !await CliHost.TrySaveSessionAsync(store, session, path, Console.Error).ConfigureAwait(false))
            {
                return ExitCodes.SessionIo;
            }
        }

        if (cli.SaveSession)
        {
            if (!await CliHost.TrySaveSessionAsync(store, session, path, Console.Error).ConfigureAwait(false))
            {
                return ExitCodes.SessionIo;
            }

            if (!cli.Quiet)
            {
                Console.Error.WriteLine($"session: {path}");
            }
        }

        return ExitCodes.Success;
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
            return (new AgentSession(workspace, model, cli.Approval, options.SystemPrompt), ExitCodes.Success);
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

            if (!CliHost.TryEnsureCredentials(resumedModel, credentials, Console.Error))
            {
                return (null, ExitCodes.Credentials);
            }

            if (!cli.Quiet)
            {
                Console.Error.WriteLine(
                    $"resumed {path} ({session.Messages.Count} message(s)) on {resumedModel.QualifiedName}");
            }

            return (session, ExitCodes.Success);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine(
                $"error: the session at '{path}' names a model or workspace this build cannot open: {ex.Message}");
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
    /// <returns>Whether to keep reading, and the report <c>/files</c> should describe.</returns>
    private static (bool KeepGoing, AgentReport? Last) HandleCommand(
        string line,
        AgentSession session,
        AgentReport? last,
        CliOptions cli,
        string path)
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
            SwitchModel(session, argument);
            return (true, last);
        }

        if (Is(command, "/approval"))
        {
            if (CliOptions.TryParseApproval(argument, out var mode))
            {
                session.Approval = mode;
                Console.Error.WriteLine($"approval: {Name(mode)}");
            }
            else
            {
                Console.Error.WriteLine("usage: /approval readonly|ask|allow");
            }

            return (true, last);
        }

        if (Is(command, "/usage"))
        {
            PrintUsage(session);
            return (true, last);
        }

        if (Is(command, "/files"))
        {
            PrintFiles(last);
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
    private static void SwitchModel(AgentSession session, string argument)
    {
        if (argument.Length == 0)
        {
            Console.Out.WriteLine(session.ActiveModel.QualifiedName);
            return;
        }

        if (ModelCatalog.TryParseQualified(argument, out var qualified))
        {
            Apply(session, qualified!);
            return;
        }

        if (argument.Contains(':', StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"unknown model '{argument}'. Name a known provider, for example grok:grok-4.");
            return;
        }

        // A bare identifier keeps the provider: "/model deepseek-reasoner" while on deepseek.
        try
        {
            Apply(session, ModelCatalog.DescriptorFor(session.ActiveModel.Provider, argument));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    private static void Apply(AgentSession session, ModelDescriptor model)
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
        Console.Out.WriteLine($"model: {model.QualifiedName}");
    }

    private static void PrintUsage(AgentSession session)
    {
        var usage = session.UsageByModel;
        if (usage.Count == 0)
        {
            Console.Out.WriteLine("no usage reported yet");
            return;
        }

        foreach (var row in usage)
        {
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.ModelId}  {Count(row.InputTokens)} in / {Count(row.OutputTokens)} out  {row.Turns} turn(s)"));
        }

        Console.Out.WriteLine($"total  {session.TotalUsage}");
    }

    private static void PrintFiles(AgentReport? last)
    {
        if (last is null)
        {
            Console.Out.WriteLine("no turn has run yet");
            return;
        }

        var files = last.FilesTouched;
        var commands = last.CommandsRun;
        if (files.Count == 0 && commands.Count == 0)
        {
            Console.Out.WriteLine("the last turn changed no files and ran no commands");
            return;
        }

        foreach (var file in files)
        {
            Console.Out.WriteLine($"file     {file}");
        }

        foreach (var command in commands)
        {
            Console.Out.WriteLine($"command  {command}");
        }
    }

    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static string Name(ApprovalMode mode) => mode switch
    {
        ApprovalMode.ReadOnly => "readonly",
        ApprovalMode.AllowAll => "allow",
        _ => "ask",
    };

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
