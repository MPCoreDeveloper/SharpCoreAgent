using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Configuration;
using SharpCoreAgent.Infrastructure.Images;

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
    /// <param name="settingsPath">
    /// The settings file this process resolved, which a pasted key is recorded in; null resolves it again.
    /// </param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ExecuteAsync(CliOptions cli, CancellationToken cancellationToken, string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(cli);

        if (!CliHost.TryOpenWorkspace(cli, Console.Error, out var workspace))
        {
            return ExitCodes.Usage;
        }

        using var provider = CliHost.Build(cli);
        var baseModel = provider.GetRequiredService<ModelDescriptor>();

        // The role decides which model a turn of this mode runs on; with no role named it is the model the
        // command line resolved, exactly as it was before roles existed.
        var model = AgentRoles.ForMode(cli, cli.Mode, baseModel);

        // Every model this process may use is checked, not just this one: --planner grok with no XAI_API_KEY
        // is a session that would fail on its first plan turn, and saying so now is the whole point.
        if (!CliHost.TryEnsureCredentials(
            AgentRoles.RequestModels(cli, baseModel),
            provider.GetRequiredService<ICredentialStore>(),
            Console.Error,
            cli.Fallback,
            CredentialPrompt.ForConsole(cli, settingsPath ?? AgentSettingsFile.PathFromEnvironment())))
        {
            return ExitCodes.Credentials;
        }

        // Before the agent: the registry is built from the tools the servers offer, so a server that will not
        // start has to be a message and exit 2 rather than an exception inside the first turn.
        if (!await CliHost.TryStartMcpAsync(cli, provider, Console.Error, cancellationToken).ConfigureAwait(false))
        {
            return ExitCodes.Usage;
        }

        var options = provider.GetRequiredService<AgentOptions>();
        var agent = provider.GetRequiredService<CodingAgent>();

        var images = new List<ImageContent>();
        var stdinImage = false;
        foreach (var path in cli.Images)
        {
            if (string.Equals(path, "-", StringComparison.Ordinal))
            {
                if (stdinImage)
                {
                    await Console.Error.WriteLineAsync(
                        "error: --image - may be used once; stdin can only be read once.".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                if (!Console.IsInputRedirected)
                {
                    await Console.Error.WriteLineAsync(
                        "error: --image - needs an image piped or pasted into stdin, but stdin is a terminal.".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                stdinImage = true;
                using var stdin = Console.OpenStandardInput();
                if (!ImageFile.TryLoadStdin(stdin, out var piped, out var pipedError))
                {
                    await Console.Error.WriteLineAsync($"error: {pipedError}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                images.Add(piped);
                continue;
            }

            if (!ImageFile.TryLoad(workspace!, path, out var image, out var error))
            {
                await Console.Error.WriteLineAsync($"error: {error}".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            images.Add(image);
        }

        if (images.Count > 0 && !model.SupportsImages)
        {
            await Console.Error.WriteLineAsync(
                $"error: {model.QualifiedName} is declared text-only in this build and cannot read an attached image.".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        var imageBytes = images.Sum(image => image.ByteCount);
        if (options.MaxTurnImageBytes > 0 && imageBytes > options.MaxTurnImageBytes)
        {
            await Console.Error.WriteLineAsync(
                $"error: the attached images total {imageBytes} bytes; this build allows {options.MaxTurnImageBytes} bytes per turn.".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

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


        var report = await agent.RunTurnAsync(session, cli.Prompt, images, observer, cancellationToken).ConfigureAwait(false);

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
