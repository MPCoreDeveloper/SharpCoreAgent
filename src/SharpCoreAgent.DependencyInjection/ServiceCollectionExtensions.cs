using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;
using SharpCoreAgent.Infrastructure.Sessions;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.DependencyInjection;

/// <summary>
/// The composition root: the one place that knows which concrete adapter satisfies which port.
/// </summary>
/// <remarks>
/// Everything registered here is either a stateless singleton or a per-run value, because a turn owns
/// its state in an <see cref="AgentSession"/> rather than in the container. That keeps the container
/// free of a captive transcript and lets one process serve many sessions.
/// <para>
/// Two deliberate choices are worth knowing before changing this file. The first is that
/// <see cref="SharpCoreAgentOptions.Descriptor"/> and <see cref="SharpCoreAgentOptions.ToAgentOptions"/> are
/// evaluated while registering, so a bad provider, model or budget fails at startup with a stack trace
/// pointing at the host's configuration rather than three seconds into a turn. The second is that
/// <see cref="IToolApprover"/> defaults to <see cref="DenyAllApprover"/>: a host that wants approval
/// prompts registers its own implementation, and a host that forgot gets refusals instead of writes.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Gets the name of the <see cref="HttpClient"/> every model adapter shares.</summary>
    public const string HttpClientName = "sharpcoreagent";

    /// <summary>
    /// Registers the agent, its tools, the session store and the model resolver.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="configure">Applies host settings; null keeps the documented defaults.</param>
    /// <returns>The same container, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">The configured provider is not served by this build.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured budget is out of range.</exception>
    public static IServiceCollection AddSharpCoreAgent(
        this IServiceCollection services,
        Action<SharpCoreAgentOptions>? configure = null)
    {
        var options = new SharpCoreAgentOptions();
        configure?.Invoke(options);
        return services.AddSharpCoreAgent(options);
    }

    /// <summary>
    /// Registers the agent with options that are already built.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="options">The host's settings, taken as they are.</param>
    /// <returns>The same container, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">The configured provider is not served by this build.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured budget is out of range.</exception>
    /// <remarks>
    /// The overload exists for a host whose settings are already a value, such as a parsed command
    /// line: passing the record avoids copying every field into a configuration lambda, and with it the
    /// silent bug where a new field is forgotten.
    /// </remarks>
    public static IServiceCollection AddSharpCoreAgent(this IServiceCollection services, SharpCoreAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Resolved here, not on first use: a typo in --provider should not surface mid-turn.
        var descriptor = options.Descriptor;
        var agentOptions = options.ToAgentOptions();

        // CodingAgent takes an optional ILogger, and the built-in container treats an optional
        // parameter as required; a host that never configured logging gets a silent sink instead.
        services.TryAddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddSingleton(options);
        services.AddSingleton(agentOptions);
        services.AddSingleton(descriptor);
        services.AddSingleton<ICredentialStore>(_ => new EnvironmentCredentialStore());
        services.AddSingleton<ISessionStore, JsonFileSessionStore>();

        services.AddHttpClient(HttpClientName, client => client.Timeout = options.HttpTimeout);
        services.AddSingleton<IChatModelResolver>(provider => new ChatModelResolver(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<ICredentialStore>(),
            options.BaseUrl));

        AddTools(services);

        if (options.IndexEnabled)
        {
            AddSharpCoreAgentIndex(services, options);
        }

        // TryAdd, so a host that registered its own approver first is not overruled; a host that
        // registers one afterwards wins because the last registration is the one that resolves.
        services.TryAddSingleton<IToolApprover>(DenyAllApprover.Instance);
        services.AddSingleton<CodingAgent>();

        return services;
    }

    /// <summary>
    /// Registers the fourteen workspace tools and the registry that advertises them.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <remarks>
    /// The tools are singletons because they are stateless: each call resolves every path through the
    /// workspace it is handed, so one instance serves every session in the process. The five index-backed
    /// tools take an optional <c>ICodeIndex</c>, which the built-in container leaves null when the host
    /// registered none — that is how <c>--no-index</c> reaches them without a flag they have to check.
    /// <para>
    /// The three shell tools share one <see cref="ShellProcessRegistry"/> on purpose: a handle returned by
    /// <c>run_shell</c> is meaningless to a <c>shell_output</c> that owns a different registry, and the
    /// registry is what kills the children when the container is disposed.
    /// </para>
    /// </remarks>
    private static void AddTools(IServiceCollection services)
    {
        services.AddSingleton<ShellProcessRegistry>();
        services.AddSingleton<ITool, ListDirectoryTool>();
        services.AddSingleton<ITool, ReadFileTool>();
        services.AddSingleton<ITool, SearchTextTool>();
        services.AddSingleton<ITool, WriteFileTool>();
        services.AddSingleton<ITool, ReplaceInFileTool>();
        services.AddSingleton<ITool, DeleteFileTool>();
        services.AddSingleton<ITool>(provider => new RunShellCommandTool(provider.GetRequiredService<ShellProcessRegistry>()));
        services.AddSingleton<ITool>(provider => new ShellOutputTool(provider.GetRequiredService<ShellProcessRegistry>()));
        services.AddSingleton<ITool>(provider => new ShellStopTool(provider.GetRequiredService<ShellProcessRegistry>()));
        services.AddSingleton<ITool, SearchCodeTool>();
        services.AddSingleton<ITool, FindSymbolTool>();
        services.AddSingleton<ITool, FindReferencesTool>();
        services.AddSingleton<ITool, CodeContextTool>();
        services.AddSingleton<ITool, IndexStatusTool>();

        // Built once from the registered tools; ToolRegistry rejects duplicate names, which turns a
        // copy-pasted registration into a startup failure instead of a model-visible ambiguity.
        services.AddSingleton<IToolRegistry>(provider => new ToolRegistry(provider.GetServices<ITool>()));
    }

    /// <summary>
    /// Registers the code index: one handle, two views, and the workspace it describes.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="options">The host's settings; <see cref="SharpCoreAgentOptions.WorkspaceRoot"/> names the tree.</param>
    /// <returns>The same container, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <remarks>
    /// The handle is a singleton because the index is one file with one writer lock: two instances in one process would
    /// be two handles on the same lock, and a build could deadlock against itself. Both interfaces resolve to that one
    /// instance, so a tool and a turn-start refresh share the connection instead of racing for it.
    /// <para>
    /// Opening is lazy — standing up this registration creates no directory and reads no file — so a host that never
    /// indexes anything costs one object.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSharpCoreAgentIndex(
        this IServiceCollection services,
        SharpCoreAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var workspace = options.CreateWorkspace();

        services.AddSingleton(_ => SharpCoreAgentIndex.Open(workspace));
        services.AddSingleton<ICodeIndex>(provider => provider.GetRequiredService<SharpCoreAgentIndex>());
        services.AddSingleton<ICodeIndexMaintainer>(provider => provider.GetRequiredService<SharpCoreAgentIndex>());
        services.AddSingleton(workspace);

        return services;
    }
}
