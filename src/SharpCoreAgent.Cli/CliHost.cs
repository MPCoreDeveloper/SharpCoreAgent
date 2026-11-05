using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The pieces both turn-running commands share: the container, the workspace, the credential preflight
/// and transcript I/O.
/// </summary>
/// <remarks>
/// Everything here fails in a way the process can report: a missing directory, a missing key or an
/// unreadable transcript becomes a message and a returned <see langword="false"/> instead of an
/// exception, because those are the failures a developer can actually fix. Anything else is a bug and
/// is allowed to escape to <see cref="Program"/>, where <c>--verbose</c> prints its stack trace.
/// </remarks>
internal static class CliHost
{
    /// <summary>
    /// Builds the container for one process run.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns>A container with the agent, its tools and the console approver registered.</returns>
    /// <exception cref="ArgumentException">The provider is not served by this build.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured budget is out of range.</exception>
    public static ServiceProvider Build(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var services = new ServiceCollection();
        services.AddSharpCoreAgent(options.ToSharpCoreAgentOptions());

        // Registered after AddSharpCoreAgent, which is what replaces the deny-all default: a developer
        // running the CLI asked to be prompted, so the terminal is the approver.
        services.AddSingleton<IToolApprover>(new ConsoleApprover(
            Console.In,
            Console.Error,
            () => !Console.IsInputRedirected,
            ThemeFor(options)));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the palette a command line asked for.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns>The theme every renderer in this process shares.</returns>
    public static AnsiTheme ThemeFor(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return AnsiTheme.Create(options.Color, Console.IsOutputRedirected);
    }

    /// <summary>
    /// Opens the workspace the tools are confined to, with every read-only root attached.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="error">Where the reason goes when the directory cannot be opened.</param>
    /// <param name="workspace">The workspace, when the call succeeds.</param>
    /// <returns><see langword="true"/> when the workspace and every attachment exist and are directories.</returns>
    /// <remarks>
    /// The workspace comes from the container's own options, so the value a command hands to
    /// <c>AgentSession</c> is the one the index registration also builds. A bad attachment root arrives as the
    /// same <see cref="DirectoryNotFoundException"/> a bad <c>--workspace</c> does, so a root that does not exist
    /// fails here — before the first request — and needs no new error plumbing: the message already names the path.
    /// </remarks>
    public static bool TryOpenWorkspace(CliOptions options, TextWriter error, out Workspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            workspace = options.ToSharpCoreAgentOptions().CreateWorkspace();
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"error: {ex.Message}");
            workspace = null;
            return false;
        }
    }

    /// <summary>
    /// Starts the MCP servers this invocation named, before anything resolves the tool registry.
    /// </summary>
    /// <param name="options">The parsed command line; without <c>--mcp</c> this does nothing at all.</param>
    /// <param name="provider">The container, for the host the composition root registered.</param>
    /// <param name="error">Where the reason goes when a server will not start.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>
    /// <see langword="true"/> when no server was named, or when every one of them answered; <see langword="false"/>
    /// when one of them did not, in which case nothing is left running and the message names the command line.
    /// </returns>
    /// <remarks>
    /// This runs before the agent is resolved because the registry is built from the tools the servers offer: the
    /// alternative is an exception three frames into the first turn, where the message no longer names the command
    /// the developer typed. Startup failure is exit 2 with one line, which is what makes a typo in a command line a
    /// typo rather than a mystery.
    /// </remarks>
    public static async Task<bool> TryStartMcpAsync(
        CliOptions options,
        ServiceProvider provider,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(error);

        if (options.McpServers.Count == 0 && options.McpUrls.Count == 0 && options.McpSseUrls.Count == 0 && !options.Browser)
        {
            return true;
        }

        if (provider.GetService<McpToolHost>() is not { } host)
        {
            await error.WriteLineAsync("error: an MCP server was given, but this build registered no MCP client.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return false;
        }

        // A server's stderr is what a developer chasing a broken one needs and is exactly what a transcript must
        // never carry, so it goes to the terminal only under --verbose.
        if (options.Verbose)
        {
            var theme = ThemeFor(options);
            host.Verbose(message => error.WriteLine($"{theme.Dim("mcp:")} {message}"));
        }

        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (McpStartupException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Checks that the provider's credential is configured, without ever printing it.
    /// </summary>
    /// <param name="model">The model the turn will use.</param>
    /// <param name="credentials">The credential source.</param>
    /// <param name="error">Where the missing key's name goes.</param>
    /// <param name="fallback">
    /// The model a refused request would be replayed at, when the developer named one. It is checked
    /// here too: a fallback whose key is missing cannot cover the failure it exists for, and saying so
    /// before the first turn beats saying it after a 401.
    /// </param>
    /// <returns><see langword="true"/> when nothing is missing.</returns>
    public static bool TryEnsureCredentials(
        ModelDescriptor model,
        ICredentialStore credentials,
        TextWriter error,
        ModelDescriptor? fallback = null) =>
        TryEnsureCredentials([model], credentials, error, fallback);

    /// <summary>
    /// Checks every model the process may send a request to, and offers to take a key for whichever
    /// provider has none.
    /// </summary>
    /// <param name="models">The models the session may use: the session's own, every named role, and the fallback.</param>
    /// <param name="credentials">The credential source.</param>
    /// <param name="error">Where a missing key's name and the three routes go.</param>
    /// <param name="fallback">The fallback model, when it is not already in <paramref name="models"/>.</param>
    /// <param name="prompt">
    /// The interactive offer to paste a key, or <see langword="null"/> for the up-front refusal a script
    /// gets. It is consulted only when something is actually missing.
    /// </param>
    /// <returns><see langword="true"/> when every model has a key, by configuration or by a paste.</returns>
    /// <remarks>
    /// Every role is checked, not just the model the first turn runs on: a planner whose key is missing is a
    /// session that starts and then fails on the first <c>/mode plan</c>, which is the failure this preflight
    /// exists to move to the front.
    /// </remarks>
    public static bool TryEnsureCredentials(
        IReadOnlyList<ModelDescriptor> models,
        ICredentialStore credentials,
        TextWriter error,
        ModelDescriptor? fallback = null,
        CredentialPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(error);

        var wanted = fallback is null ? models : [.. models, fallback];
        var missing = wanted.Where(model => !HasCredential(model, credentials)).ToArray();
        if (missing.Length == 0)
        {
            return true;
        }

        if (prompt is not null && prompt.TryFix(missing, credentials))
        {
            return true;
        }

        // Re-checked because a developer may have pasted a key for one provider and declined the next: the
        // refusal names only what is still missing, and one provider once however many roles name it.
        var stillMissing = wanted
            .Where(model => !HasCredential(model, credentials))
            .DistinctBy(model => model.Provider)
            .ToArray();

        foreach (var model in stillMissing)
        {
            Report(model, error);
        }

        return stillMissing.Length == 0;
    }

    /// <summary>
    /// Answers whether a model's provider already has a key, without reporting anything.
    /// </summary>
    /// <param name="model">The model to check.</param>
    /// <param name="credentials">The credential source.</param>
    /// <returns><see langword="true"/> when a key is set, or the provider needs none.</returns>
    private static bool HasCredential(ModelDescriptor model, ICredentialStore credentials)
    {
        var names = ModelCatalog.CredentialNames(model.Provider);
        return names.Count == 0 || names.Any(name => credentials.Find(name) is not null);
    }

    /// <summary>
    /// Names the missing credential and the three routes that fix it.
    /// </summary>
    /// <param name="model">The model whose provider has no key.</param>
    /// <param name="error">Where the lines go.</param>
    /// <remarks>
    /// Three routes because "set the variable" is the least durable one, and a developer who just watched a
    /// session refuse to start deserves to know the one that survives a restart.
    /// </remarks>
    private static void Report(ModelDescriptor model, TextWriter error)
    {
        var names = ModelCatalog.CredentialNames(model.Provider);
        error.WriteLine(
            $"error: {model.Provider} needs a credential and none is set for {model.QualifiedName}.");
        error.WriteLine($"hint: set {string.Join(" or ", names)}, or write it to a file as {names[0]}=<key> and pass --credential-file <path>, or run 'sharpcoreagent config set credential-file <path>' once.");
    }

    /// <summary>
    /// Writes the transcript.
    /// </summary>
    /// <param name="store">The session store.</param>
    /// <param name="session">The conversation to persist.</param>
    /// <param name="path">The destination, relative to the current directory or absolute.</param>
    /// <param name="error">Where the reason goes when the write fails.</param>
    /// <returns><see langword="true"/> when the file is in place.</returns>
    /// <remarks>
    /// The write is deliberately not cancellable: it usually runs because the developer interrupted the
    /// turn, and a transcript lost exactly when it is most interesting is worse than a write that
    /// outlives the Ctrl+C.
    /// </remarks>
    public static async Task<bool> TrySaveSessionAsync(
        ISessionStore store,
        AgentSession session,
        string path,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            await store.SaveAsync(
                session.ToSnapshot(DateTimeOffset.UtcNow),
                Path.GetFullPath(path),
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SharpCoreAgentException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync($"error: could not write the session to '{path}': {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Reads a transcript.
    /// </summary>
    /// <param name="store">The session store.</param>
    /// <param name="path">The file to read.</param>
    /// <param name="error">Where the reason goes when the read fails.</param>
    /// <returns>The snapshot, or <see langword="null"/> after reporting why it could not be read.</returns>
    public static async Task<SessionSnapshot?> TryLoadSessionAsync(ISessionStore store, string path, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return await store.LoadAsync(Path.GetFullPath(path), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SharpCoreAgentException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync($"error: could not read the session at '{path}': {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }
}
