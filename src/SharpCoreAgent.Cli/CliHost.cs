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
            () => !Console.IsInputRedirected));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Opens the workspace the tools are confined to.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="error">Where the reason goes when the directory cannot be opened.</param>
    /// <param name="workspace">The workspace, when the call succeeds.</param>
    /// <returns><see langword="true"/> when the workspace exists and is a directory.</returns>
    public static bool TryOpenWorkspace(CliOptions options, TextWriter error, out Workspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            workspace = Workspace.Create(options.Workspace);
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
    /// <returns><see langword="true"/> when nothing is missing.</returns>
    public static bool TryEnsureCredentials(ModelDescriptor model, ICredentialStore credentials, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(error);

        var names = ModelCatalog.CredentialNames(model.Provider);
        if (names.Count == 0 || names.Any(name => credentials.Find(name) is not null))
        {
            return true;
        }

        error.WriteLine(
            $"error: {model.Provider} needs a credential and none is set. Set {string.Join(" or ", names)} and try again.");
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
            error.WriteLine($"error: could not write the session to '{path}': {ex.Message}");
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
            error.WriteLine($"error: could not read the session at '{path}': {ex.Message}");
            return null;
        }
    }
}
