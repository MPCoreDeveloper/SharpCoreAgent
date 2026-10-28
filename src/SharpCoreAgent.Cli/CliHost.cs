using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
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
        ModelDescriptor? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(error);

        return HasCredential(model, credentials, error)
            && (fallback is null || HasCredential(fallback, credentials, error));
    }

    /// <summary>
    /// Checks one model's provider and reports the keys it would read.
    /// </summary>
    /// <param name="model">The model to check.</param>
    /// <param name="credentials">The credential source.</param>
    /// <param name="error">Where the missing key's name goes.</param>
    /// <returns><see langword="true"/> when a key is set, or the provider needs none.</returns>
    /// <remarks>
    /// A fallback on the same provider as the primary is checked twice; that costs two dictionary
    /// lookups and keeps the rule readable.
    /// </remarks>
    private static bool HasCredential(ModelDescriptor model, ICredentialStore credentials, TextWriter error)
    {
        var names = ModelCatalog.CredentialNames(model.Provider);
        if (names.Count == 0 || names.Any(name => credentials.Find(name) is not null))
        {
            return true;
        }

        // Three routes, because "set the variable" is the least durable one and a developer who just
        // watched a session refuse to start deserves to know the one that survives a restart.
        error.WriteLine(
            $"error: {model.Provider} needs a credential and none is set for {model.QualifiedName}.");
        error.WriteLine($"hint: set {string.Join(" or ", names)}, or write it to a file as {names[0]}=<key> and pass --credential-file <path>, or run 'sharpcoreagent config set credential-file <path>' once.");
        return false;
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
