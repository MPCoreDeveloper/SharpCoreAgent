using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Infrastructure.Code;
using SharpCoreAgent.Infrastructure.Checkpoints;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.McpServer;
using SharpCoreAgent.Infrastructure.Providers;
using SharpCoreAgent.Infrastructure.Sessions;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Infrastructure.Web;
using SharpCoreAgent.Languages.CSharp;
using SharpCoreAgent.Languages.FSharp;

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
        services.AddSingleton<ICredentialStore>(_ => EnvironmentCredentialStore.FromFile(options.CredentialFilePath));
        services.AddSingleton<ISessionStore, JsonFileSessionStore>();

        services.AddHttpClient(HttpClientName, client => client.Timeout = options.HttpTimeout);
        services.AddSingleton<IChatModelResolver>(provider => new ChatModelResolver(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<ICredentialStore>(),
            options.BaseUrl,
            fallback: options.Fallback));

        AddTools(services);

        services.AddSingleton<TurnCheckpointStore>();
        services.AddSingleton<ITurnCheckpointJournal>(provider => provider.GetRequiredService<TurnCheckpointStore>());

        if (options.GitToolsEnabled)
        {
            AddGit(services);
        }

        if (options.WebAccessEnabled)
        {
            AddWeb(services, options);
        }

        if (options.McpServers.Count > 0 || options.McpUrls.Count > 0 || options.McpSseUrls.Count > 0)
        {
            AddMcp(services, options);
        }

        if (options.IndexEnabled)
        {
            AddSharpCoreAgentIndex(services, options);
        }

        if (options.CSharpToolsEnabled || options.FSharpToolsEnabled)
        {
            AddCodeIntelligence(services, options);
        }

        // TryAdd, so a host that registered its own approver first is not overruled; a host that
        // registers one afterwards wins because the last registration is the one that resolves.
        services.TryAddSingleton<IToolApprover>(DenyAllApprover.Instance);
        services.AddSingleton<CodingAgent>();

        return services;
    }

    /// <summary>
    /// Registers the nineteen workspace tools and the registry that advertises them.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <remarks>
    /// <c>fetch_url</c> is not one of them: it is registered by <see cref="AddWeb"/> only when the developer asked for the
    /// network, so this count is the workspace surface and stays true with <c>--web</c> on or off. The number is the
    /// surface a default build advertises — fifteen tools from rounds 1–3, <c>dotnet_verify</c> from <c>012</c>,
    /// <c>python_verify</c> and <c>node_verify</c> from <c>040</c>, <c>task</c> from <c>011</c>, <c>view_image</c>
    /// from the image round and <c>ocr_image</c> from T026 — and <c>TaskToolRegistrationTests</c> pins the whole set, so a tool cannot be added or
    /// dropped without a failing test. <see cref="AddCodeIntelligence"/> adds the three language-neutral code tools by
    /// default, making the default process surface twenty-six.
    ///
    /// The tools are singletons because they are stateless: each call resolves every path through the
    /// workspace it is handed, so one instance serves every session in the process. The five index-backed
    /// tools take an optional <c>ICodeIndex</c>, which the built-in container leaves null when the host
    /// registered none — that is how <c>--no-index</c> reaches them without a flag they have to check.
    /// <para>
    /// <c>view_image</c> is unconditional for the reason <c>dotnet_verify</c> is: reading a picture is a read. The
    /// capability gate lives in the tool, not in the container — it refuses when the session's own
    /// <see cref="SharpCoreAgent.Domain.Model.ModelDescriptor.SupportsImages"/> is false, which the container cannot
    /// know at registration time because the model is chosen per session.
    /// </para>
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
        services.AddSingleton<ITool, ViewImageTool>();
        services.AddSingleton<ITool, OcrImageTool>();
        services.AddSingleton<ITool, SearchTextTool>();
        services.AddSingleton<ITool, ListRootsTool>();
        services.AddSingleton<ITool, ListArtifactsTool>();
        services.AddSingleton<ITool, WriteFileTool>();
        services.AddSingleton<ITool, ReplaceInFileTool>();
        services.AddSingleton<ITool, DeleteFileTool>();
        services.AddSingleton<ITool>(provider => new RunShellCommandTool(provider.GetRequiredService<ShellProcessRegistry>()));
        services.AddSingleton<ITool>(provider => new ShellOutputTool(provider.GetRequiredService<ShellProcessRegistry>()));
        services.AddSingleton<ITool>(provider => new ShellStopTool(provider.GetRequiredService<ShellProcessRegistry>()));

        // Beside run_shell rather than behind a flag: the prompt tells the model to verify a .NET edit with
        // this tool, and a rule whose tool can be absent teaches the model that instructions are approximate.
        services.AddSingleton<ITool>(provider => new DotnetVerifyTool(provider.GetRequiredService<ShellProcessRegistry>()));

        // The same rule for the two other runtime profiles. They share the shell's process registry so a test
        // run is tracked and killed with every other child this process owns.
        services.AddSingleton<ITool>(provider => new PythonVerifyTool(provider.GetRequiredService<ShellProcessRegistry>()));
        services.AddSingleton<ITool>(provider => new NodeVerifyTool(provider.GetRequiredService<ShellProcessRegistry>()));

        // Delegation is unconditional, beside the tools it hands on: the prompt tells the model when to call it, and a
        // rule whose tool can be absent is a rule the model learns to ignore. The registry arrives as a factory because
        // a tool cannot be handed the set that contains it while that set is being built — the container reports a
        // circular dependency and every host fails at startup (F-1) — and because --mcp replaces the registry after
        // startup, so a child sees the servers' tools too (F-2).
        services.AddSingleton<ITool>(provider => new TaskTool(
            provider.GetRequiredService<IChatModelResolver>(),
            () => provider.GetRequiredService<IToolRegistry>(),
            provider.GetRequiredService<AgentOptions>(),
            provider.GetRequiredService<ITurnCheckpointJournal>()));

        services.AddSingleton<ITool, SearchCodeTool>();
        services.AddSingleton<ITool, FindSymbolTool>();
        services.AddSingleton<ITool, FindReferencesTool>();
        services.AddSingleton<ITool, CodeContextTool>();
        services.AddSingleton<ITool, IndexStatusTool>();

        // Built once from the registered tools; ToolRegistry rejects duplicate names, which turns a
        // copy-pasted registration into a startup failure instead of a model-visible ambiguity.
        services.AddSingleton<IToolRegistry>(provider => new ToolRegistry(provider.GetServices<ITool>()));
    }

    /// <summary>Registers the two read-only Git evidence tools over the shell process host.</summary>
    /// <param name="services">The container to add to.</param>
    private static void AddGit(IServiceCollection services)
    {
        services.AddSingleton<ITool>(provider => new GitStatusTool(provider.GetRequiredService<ShellProcessRegistry>()));
        services.AddSingleton<ITool>(provider => new GitDiffTool(provider.GetRequiredService<ShellProcessRegistry>()));
    }

    /// <summary>
    /// Registers the language adapters the host enabled, the router over them, and the three language-neutral tools.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="options">The host's settings; the workspace comes from <see cref="SharpCoreAgentOptions.CreateWorkspace"/>.</param>
    /// <remarks>
    /// C# is on by default and F# is opt-in. When neither is enabled there is no router and no tool to call. Adapter order is
    /// C# then F#; their extensions do not overlap, and the router's first-match rule becomes relevant only if a later
    /// adapter claims one of those extensions.
    /// <para>
    /// The adapters are singletons because each caches a loaded project graph. The tools never learn that a language
    /// exists: they are handed the router through all three ports, so a third language remains a registration line.
    /// </para>
    /// </remarks>
    private static void AddCodeIntelligence(IServiceCollection services, SharpCoreAgentOptions options)
    {
        services.TryAddSingleton(options.CreateWorkspace());

        if (options.CSharpToolsEnabled)
        {
            services.TryAddSingleton<CSharpCodeIntelligence>();
            services.AddSingleton<ILanguageAdapter>(provider => provider.GetRequiredService<CSharpCodeIntelligence>());
        }

        if (options.FSharpToolsEnabled)
        {
            services.TryAddSingleton<FsCodeIntelligence>();
            services.AddSingleton<ILanguageAdapter>(provider => provider.GetRequiredService<FsCodeIntelligence>());
        }

        services.TryAddSingleton<LanguageRouter>();
        services.TryAddSingleton<ICodeDiagnostics>(provider => provider.GetRequiredService<LanguageRouter>());
        services.TryAddSingleton<ISymbolUses>(provider => provider.GetRequiredService<LanguageRouter>());
        services.TryAddSingleton<ISignatureHelp>(provider => provider.GetRequiredService<LanguageRouter>());

        services.AddSingleton<ITool, FileDiagnosticsTool>();
        services.AddSingleton<ITool, UsesAtTool>();
        services.AddSingleton<ITool, SignatureAtTool>();
    }

    /// <summary>
    /// Registers <c>fetch_url</c>: its client, its ceilings and its address resolver.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="options">The host's settings; the named hosts come from <see cref="SharpCoreAgentOptions.WebAllowedHosts"/>.</param>
    /// <remarks>
    /// Called only when the developer asked for the network, so a process that did not ask for it has no web client, no
    /// fetch tool and no <see cref="ITool"/> that could reach anything. That is a stronger guarantee than a tool that
    /// checks a flag at call time: the tool does not exist to be called.
    /// <para>
    /// The client is deliberately its own. It carries no default header, no cookie container, no automatic redirect and
    /// no proxy, because the tool counts and judges each hop itself — and because the model adapters' client carries an
    /// <c>Authorization</c> header that must never travel to a third-party page.
    /// </para>
    /// <para>
    /// The handler makes the address judgement a second time, at connect time, through
    /// <see cref="WebFetchHandler.Create"/>: a name that resolved publicly when the hop was checked and privately when
    /// the socket was opened is refused rather than followed.
    /// </para>
    /// </remarks>
    private static void AddWeb(IServiceCollection services, SharpCoreAgentOptions options)
    {
        services.AddHttpClient(WebFetchOptions.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider => WebFetchHandler.Create(
                provider.GetRequiredService<IWebAddressResolver>(),
                provider.GetRequiredService<WebFetchOptions>()));

        services.TryAddSingleton<IWebAddressResolver>(DnsWebAddressResolver.Instance);
        services.AddSingleton(new WebFetchOptions { AllowedHosts = options.WebAllowedHosts });
        services.AddSingleton<IBrowserFetcher>(provider =>
            provider.GetService<McpToolHost>() is { } host
                ? new McpBrowserFetcher(host)
                : UnavailableBrowserFetcher.Instance);
        services.AddSingleton<ITool, FetchUrlTool>();
    }

    /// <summary>
    /// Registers the MCP client: one host, the ceilings it works within, and the registry that appends the
    /// tools the servers offer.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="options">The host's settings; <see cref="SharpCoreAgentOptions.McpServers"/> names the command lines.</param>
    /// <remarks>
    /// Called only when a server was named, which is the same structural promise <see cref="AddWeb"/> makes: a
    /// process that asked for no server has no client, no host and no <see cref="ITool"/> that could start one.
    /// <para>
    /// The host is a singleton because it owns processes that outlive a tool call, and the container disposes it
    /// — killing every child — when the process ends. The registry is replaced rather than added to, because
    /// the tool list is the union of the built-ins and whatever the servers offered at startup, which cannot be
    /// known before they have answered; that is why this factory starts the host rather than trusting a
    /// resolution order nobody can see.
    /// </para>
    /// </remarks>
    private static void AddMcp(IServiceCollection services, SharpCoreAgentOptions options)
    {
        services.AddSingleton(new McpClientOptions { SamplingPolicy = options.McpSampling });
        services.AddSingleton(provider => new McpToolHost(
            options.McpServers,
            provider.GetRequiredService<McpClientOptions>(),
            provider.GetRequiredService<IChatModelResolver>(),
            urls: options.McpUrls,
            sseUrls: options.McpSseUrls));

        services.AddSingleton<IToolRegistry>(provider =>
        {
            var host = provider.GetRequiredService<McpToolHost>();
            return new ToolRegistry([.. provider.GetServices<ITool>(), .. host.Started().Tools]);
        });
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

    /// <summary>
    /// Registers the stdio evidence server: the index, the five read-only tools that read it, and the session that
    /// answers a client for them.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="workspace">The workspace the index describes and every call answers within.</param>
    /// <param name="options">The versions and the refresh switch, ceiling and interval the server works within.</param>
    /// <param name="log">Receives one diagnostic per refresh and per protocol oddity, or null to stay quiet.</param>
    /// <returns>The same container, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/>, <paramref name="workspace"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">The options do not validate.</exception>
    /// <remarks>
    /// What is deliberately absent is the point (FR-001): no <see cref="IChatModel"/>, no credential store, no
    /// resolver and no <see cref="HttpClient"/>. An agent starts this process dozens of times a day on a machine
    /// whose owner may never have typed a key for this product, and none is needed to answer "where is this
    /// declared".
    /// <para>
    /// The five tools are registered here rather than reused from <c>AddSharpCoreAgent</c>: that surface is
    /// twenty-six workspace tools including the ones that write, start processes and delegate, and the server's
    /// allow-list is the statement that this container holds none of them. The published set filters this registry
    /// again, so the two gates the contract requires are both present.
    /// </para>
    /// <para>
    /// The index is reached through <see cref="LeasedCodeIndex"/> and the refresh opens its own maintainer per
    /// attempt, so no database handle outlives a call: a server an editor keeps open must not stop a terminal
    /// build. The streams come from <see cref="McpServerStreams"/>, which defaults to the process's own stdio and
    /// is what a test replaces to talk to the session it just built.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMcpServer(
        this IServiceCollection services,
        Workspace workspace,
        McpServerOptions options,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(options);

        var validated = options.Validate();

        services.AddSingleton(workspace);
        services.AddSingleton(validated);
        services.TryAddSingleton(_ => McpServerStreams.Standard());

        // One factory for the whole registration, so a leased read and a refresh cannot end up describing two
        // different workspaces. Opening is free: the index creates nothing and reads nothing until its first call.
        Func<SharpCoreAgentIndex> open = () => SharpCoreAgentIndex.Open(workspace);

        services.AddSingleton<ICodeIndex>(_ => new LeasedCodeIndex(() => open()));
        services.AddSingleton<ITool, SearchCodeTool>();
        services.AddSingleton<ITool, FindSymbolTool>();
        services.AddSingleton<ITool, FindReferencesTool>();
        services.AddSingleton<ITool, CodeContextTool>();
        services.AddSingleton<ITool, IndexStatusTool>();
        services.AddSingleton<ITool>(new ViewImageTool(allowWithoutModel: true));
        services.AddSingleton<IToolRegistry>(provider => new ToolRegistry(provider.GetServices<ITool>()));

        services.TryAddSingleton(provider => new McpServerSession(
            provider.GetRequiredService<McpServerStreams>().Input,
            provider.GetRequiredService<McpServerStreams>().Output,
            provider.GetRequiredService<IToolRegistry>(),
            provider.GetRequiredService<Workspace>(),
            provider.GetRequiredService<McpServerOptions>(),
            log,
            new IndexRefreshGate(() => open(), provider.GetRequiredService<McpServerOptions>(), log)));

        return services;
    }
}
