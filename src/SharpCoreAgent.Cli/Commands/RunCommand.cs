using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>sharpcoreagent run "&lt;prompt&gt;"</c>: one turn, one transcript, one exit code.
/// </summary>
/// <remarks>
/// The exit code is the turn's outcome, not the process's mood: a turn that failed still writes its
/// transcript, because the developer usually wants to read why, and a failed save wins over a failed
/// turn because losing the record is the error that cannot be repaired afterwards.
/// </remarks>
internal static class RunCommand
{
    /// <summary>
    /// Runs one turn.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ExecuteAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cli);

        if (!CliHost.TryOpenWorkspace(cli, Console.Error, out var workspace))
        {
            return ExitCodes.Usage;
        }

        using var provider = CliHost.Build(cli);
        var model = provider.GetRequiredService<ModelDescriptor>();
        if (!CliHost.TryEnsureCredentials(model, provider.GetRequiredService<ICredentialStore>(), Console.Error, cli.Fallback))
        {
            return ExitCodes.Credentials;
        }

        var options = provider.GetRequiredService<AgentOptions>();
        var agent = provider.GetRequiredService<CodingAgent>();

        // Before the first model call, not after: a model that is handed an empty index has no way to know it should
        // have waited, and the five retrieval tools answer with "missing" for the whole turn.
        await IndexCommand.RefreshAsync(provider, cli, cli.Quiet || cli.Json, Console.Out, cancellationToken).ConfigureAwait(false);

        var session = new AgentSession(workspace!, model, cli.Approval, options.SystemPrompt) { Mode = cli.Mode };
        var observer = new ConsoleObserver(
            Console.Out,
            Console.Error,
            cli.Json,
            cli.Quiet || cli.Json,
            CliHost.ThemeFor(cli),
            verbose: cli.Verbose,
            budget: () => (session.TokensSpent, session.MaxTotalTokens ?? options.MaxTotalTokens));


        var report = await agent.RunTurnAsync(session, cli.Prompt, observer, cancellationToken).ConfigureAwait(false);

        if (cli.SaveSession)
        {
            var path = cli.SessionPath ?? CliOptions.DefaultSessionPath(workspace!.Root, session.CreatedAtUtc);
            var store = provider.GetRequiredService<ISessionStore>();
            if (!await CliHost.TrySaveSessionAsync(store, session, path, Console.Error).ConfigureAwait(false))
            {
                return ExitCodes.SessionIo;
            }

            if (!cli.Json && !cli.Quiet)
            {
                await Console.Error.WriteLineAsync($"{CliHost.ThemeFor(cli).Dim("session:")} {Path.GetFullPath(path)}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        return ExitCodes.ForReport(report);
    }
}
