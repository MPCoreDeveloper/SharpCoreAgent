using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Configuration;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>sharpcoreagent serve</c>: chat in a browser — the same workspace, model, tools and session the terminal uses,
/// answered on a loopback page.
/// </summary>
/// <remarks>
/// The command is chat with its terminal replaced: it opens the workspace and the container chat opens, checks the
/// credentials a turn would need, starts every MCP server, refreshes the index, and then answers a page instead of a
/// prompt. A turn is run on the shared <see cref="AgentSession"/> by <see cref="CodingAgent"/> with a
/// <see cref="WebObserver"/> for events and a <see cref="WebApprover"/> for the destructive question, so the browser and a
/// terminal drive one conversation rather than two. It starts no process and opens no browser: the URL it prints is the
/// developer's to open, exactly as <c>ui --serve</c> does.
/// </remarks>
internal static class ServeCommand
{
    /// <summary>
    /// Serves the conversation until the developer interrupts the process.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="scope">The Ctrl+C split between cancelling a turn and leaving.</param>
    /// <param name="settingsPath">The resolved settings file, for the credential offer; null resolves it again.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ExecuteAsync(CliOptions cli, CancellationScope scope, string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(scope);

        if (!CliHost.TryOpenWorkspace(cli, Console.Error, out var workspace))
        {
            return ExitCodes.Usage;
        }

        // `serve` starts a fresh conversation; reopening an archived one is chat's job, and saying so is clearer than
        // loading a transcript whose model this build may not serve.
        if (cli.Resume)
        {
            await Console.Error.WriteLineAsync(
                "error: serve starts a fresh conversation; use 'sharpcoreagent chat --resume' to reopen one.".AsMemory(),
                scope.ExitToken).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        // The container is built here rather than through CliHost.Build for one reason: the approver is the page. Registering
        // it after AddSharpCoreAgent replaces the deny-all default exactly as the console approver does for a turn.
        var hub = new ServeHub();
        var approver = new WebApprover(hub);
        var observer = new WebObserver(hub);

        var services = new ServiceCollection();
        services.AddSharpCoreAgent(cli.ToSharpCoreAgentOptions());
        services.AddSingleton<IToolApprover>(approver);

        using var provider = services.BuildServiceProvider();

        var baseModel = provider.GetRequiredService<ModelDescriptor>();
        var model = AgentRoles.ForMode(cli, cli.Mode, baseModel);
        var credentials = provider.GetRequiredService<ICredentialStore>();

        // Every model the session may use is checked, exactly as run and chat check it: a missing planner key is a session
        // that starts and then fails on the first plan turn, and saying so now is the point.
        if (!CliHost.TryEnsureCredentials(
            AgentRoles.RequestModels(cli, baseModel),
            credentials,
            Console.Error,
            cli.Fallback,
            CredentialPrompt.ForConsole(cli, settingsPath ?? AgentSettingsFile.PathFromEnvironment())))
        {
            return ExitCodes.Credentials;
        }

        // Before the agent, for the same reason run starts the servers before its first turn: the registry is built from the
        // tools the servers offer, and a server that will not start is exit 2 with one line rather than a mid-turn exception.
        if (!await CliHost.TryStartMcpAsync(cli, provider, Console.Error, scope.ExitToken).ConfigureAwait(false))
        {
            return ExitCodes.Usage;
        }

        var options = provider.GetRequiredService<AgentOptions>();
        var agent = provider.GetRequiredService<CodingAgent>();
        var store = provider.GetRequiredService<ISessionStore>();
        var session = new AgentSession(workspace!, model, cli.Approval, options.SystemPrompt) { Mode = cli.Mode };

        // A built index before the first request, so a question about the code lands against evidence rather than "missing".
        await IndexCommand.RefreshAsync(provider, cli, cli.Quiet || cli.Json, Console.Out, scope.ExitToken).ConfigureAwait(false);

        async Task RunTurnAsync(string prompt, CancellationToken cancellationToken)
        {
            await agent.RunTurnAsync(session, prompt, [], observer, cancellationToken).ConfigureAwait(false);

            if (cli.SaveSession)
            {
                var path = cli.SessionPath ?? CliOptions.DefaultSessionPath(workspace!.Root, session.CreatedAtUtc);
                await CliHost.TrySaveSessionAsync(store, session, path, Console.Error).ConfigureAwait(false);
            }
        }

        ServeState State() => new(
            session.ActiveModel.QualifiedName,
            ApprovalName(session.Approval),
            ModeName(session.Mode),
            session.TokensSpent,
            session.MaxTotalTokens ?? options.MaxTotalTokens);

        if (!ServeServer.TryStart(
                cli.UiPort,
                hub,
                approver,
                RunTurnAsync,
                State,
                mode => session.Approval = mode,
                mode => session.Mode = mode,
                Console.Error,
                out var server))
        {
            return ExitCodes.Usage;
        }

        using (server)
        {
            Announce(server!.Url, workspace!.Root, cli, Console.Out, Console.Error);
            await server.ServeAsync(scope.ExitToken).ConfigureAwait(false);
        }

        return ExitCodes.Success;
    }

    /// <summary>Says where the page is, and how to open it.</summary>
    /// <param name="url">The URL that is listening.</param>
    /// <param name="workspaceRoot">The workspace whose conversation is being served.</param>
    /// <param name="cli">The parsed command line, for <c>--json</c>.</param>
    /// <param name="output">Where the JSON object goes, when one was asked for.</param>
    /// <param name="error">Where the sentences go.</param>
    /// <remarks>
    /// The URL is the first line on its own, so a person can copy it and a log can be read from the top. Opening it is the
    /// developer's action: nothing here calls <c>Process.Start</c>, and the note names the two editors' own browsers because
    /// that is the workflow this command exists for.
    /// </remarks>
    private static void Announce(string url, string workspaceRoot, CliOptions cli, TextWriter output, TextWriter error)
    {
        if (cli.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["command"] = "serve",
                ["url"] = url,
                ["workspace"] = IndexStatusDocument.Slash(workspaceRoot),
            }));
        }

        error.WriteLine(url);
        error.WriteLine("note: open it in a browser, or in VS Code's Simple Browser (Ctrl+Shift+P → Simple Browser: Show) or a Visual Studio browser window");
        error.WriteLine($"note: {IndexStatusDocument.Slash(workspaceRoot)} · the page and a terminal drive the same conversation");
        error.WriteLine("note: Ctrl+C stops the server");
    }

    private static string ApprovalName(ApprovalMode mode) => mode switch
    {
        ApprovalMode.ReadOnly => "readonly",
        ApprovalMode.Ask => "ask",
        _ => "allow",
    };

    private static string ModeName(AgentMode mode) => mode == AgentMode.Plan ? "plan" : "act";
}
