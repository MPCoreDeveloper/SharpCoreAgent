using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.DependencyInjection;

/// <summary>
/// Everything a host decides before the agent exists: which model to talk to, where the tools may
/// work, and how much the turn may spend.
/// </summary>
/// <remarks>
/// The record is the single input of <see cref="ServiceCollectionExtensions.AddSharpCoreAgent(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{SharpCoreAgentOptions})"/>. It holds
/// no credential: a provider is named by key, and the key's value is read from the environment at the
/// moment a request is sent. That is what lets the CLI print this object, log it, or build it from
/// command-line arguments without ever handling a secret.
/// <para>
/// Defaults match <c>contracts/cli.md</c> so a host that configures nothing still behaves like
/// <c>sharpcoreagent run "…"</c> with no options.
/// </para>
/// </remarks>
public sealed record SharpCoreAgentOptions
{
    /// <summary>Gets the provider key. Default <c>deepseek</c>.</summary>
    public string Provider { get; init; } = ModelCatalog.DefaultProvider;

    /// <summary>Gets the model identifier, or <see langword="null"/> for the provider's default.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the directory the tools are confined to. Default the current directory.</summary>
    public string WorkspaceRoot { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets the read-only roots the read tools may also answer from, in the order the developer named them.
    /// </summary>
    /// <remarks>
    /// Empty by default, and deliberately not part of the settings file: a directory the agent may read is
    /// authority for one invocation, not a preference that outlives it. <see cref="CreateWorkspace"/> is the one
    /// place they are applied, so the workspace a session uses and the workspace the index registration creates
    /// cannot disagree about which roots exist.
    /// </remarks>
    public IReadOnlyList<string> AdditionalRoots { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether this process may read the web. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The switch that registers <c>fetch_url</c>. Off by default, and deliberately not part of the settings file or the
    /// environment: a preference that outlives one invocation is the wrong shape for a capability that reaches the
    /// internet, because the developer who turned it on for one question would never learn it stayed on.
    /// </remarks>
    public bool WebAccessEnabled { get; init; }

    /// <summary>
    /// Gets the hosts the developer named for this invocation, which the address refusal does not apply to.
    /// </summary>
    /// <remarks>
    /// Empty by default, and read only when <see cref="WebAccessEnabled"/> is set. A host, a <c>host:port</c>, or an IP
    /// literal; matched exactly and case-insensitively. There are no wildcards: naming three hosts is a decision a
    /// reviewer can check, while <c>*.internal</c> is a rule nobody reads.
    /// </remarks>
    public IReadOnlyList<string> WebAllowedHosts { get; init; } = [];

    /// <summary>Gets the approval mode. Default <see cref="ApprovalMode.Ask"/>.</summary>
    public ApprovalMode Approval { get; init; } = ApprovalMode.Ask;

    /// <summary>Gets the per-turn model round-trip ceiling. Default 400.</summary>
    public int MaxIterations { get; init; } = 400;

    /// <summary>Gets the per-request output token ceiling. Default 8 000.</summary>
    public int MaxOutputTokensPerTurn { get; init; } = 8_000;

    /// <summary>Gets the session-wide token budget. Default 10 000 000.</summary>
    public long MaxTotalTokens { get; init; } = 10_000_000;

    /// <summary>Gets the largest tool result the model is handed. Default 24 000 characters.</summary>
    public int MaxToolResultChars { get; init; } = 24_000;

    /// <summary>Gets the sampling temperature. Default 0.2.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Gets the endpoint for the <c>openai-compatible</c> provider, when one is used.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets the path of a credential file whose keys win over the environment, or <see langword="null"/>
    /// when the environment is the only source.
    /// </summary>
    /// <remarks>
    /// A path, not a value: the record stays free of secrets, so it can still be printed, logged or
    /// compared. The file is read when the credential store is resolved, which means a malformed file
    /// fails the command before the first request instead of halfway through a turn.
    /// </remarks>
    public string? CredentialFilePath { get; init; }

    /// <summary>
    /// Gets the model a refused request is replayed at, or <see langword="null"/> when a failed turn is
    /// reported rather than retried elsewhere.
    /// </summary>
    /// <remarks>
    /// The fallback is a descriptor rather than a name so the host decides it once, through the
    /// catalogue, and the resolver cannot be handed a provider this build does not serve. A fallback
    /// that names the model already in use is ignored at resolution time: a replay there would repeat
    /// the request that just failed.
    /// </remarks>
    public ModelDescriptor? Fallback { get; init; }

    /// <summary>Gets the HTTP timeout for one provider request. Default five minutes.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the system prompt override; <see langword="null"/> keeps the built-in one.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Gets a value indicating whether the code index may be read in this process. Default <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <c>--no-index</c> clears this, and the composition root then registers no <c>ICodeIndex</c> at all. That is a
    /// stronger guarantee than a flag the tools check: a tool cannot read, refresh or accidentally create an index file
    /// that was never given to it, and it answers with the documented "disabled" failure instead.
    /// </remarks>
    public bool IndexEnabled { get; init; } = true;

    /// <summary>
    /// Gets the wall-clock ceiling for the pre-turn index refresh. Default three seconds.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeSpan.Zero"/> disables the refresh and keeps every read, which is the setting for a host that
    /// indexes out of band and does not want a turn to wait for a scan.
    /// </remarks>
    public TimeSpan IndexBudget { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets the descriptor for <see cref="Provider"/> and <see cref="ModelId"/>.</summary>
    /// <exception cref="ArgumentException">The provider is not one this build serves.</exception>
    public ModelDescriptor Descriptor => ModelCatalog.DescriptorFor(Provider, ModelId);

    /// <summary>
    /// Opens the workspace the tools are confined to, with every read-only root attached.
    /// </summary>
    /// <returns>The normalised workspace.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The workspace root, or one of <see cref="AdditionalRoots"/>, is not an existing directory.
    /// </exception>
    /// <remarks>
    /// The single place attachments are applied. A host that opened a workspace itself and then built its own
    /// would be free to leave them out, and a tool would answer from fewer roots than the developer granted
    /// without anything failing; keeping the composition here makes that mistake impossible.
    /// </remarks>
    public Workspace CreateWorkspace() => Workspace.Create(WorkspaceRoot).WithAttachments(AdditionalRoots);

    /// <summary>
    /// Projects these settings onto the loop's budgets.
    /// </summary>
    /// <returns>The validated agent options.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A budget is outside its documented range.</exception>
    public AgentOptions ToAgentOptions()
    {
        return new AgentOptions
        {
            MaxIterations = MaxIterations,
            MaxOutputTokensPerTurn = MaxOutputTokensPerTurn,
            MaxTotalTokens = MaxTotalTokens,
            MaxToolResultChars = MaxToolResultChars,
            Temperature = Temperature,
            Approval = Approval,
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt)
                ? PromptLibrary.SystemPromptFor(IndexEnabled, WebAccessEnabled)
                : SystemPrompt,
        }.Validate();
    }
}

/// <summary>
/// Refuses every destructive tool call.
/// </summary>
/// <remarks>
/// The container registers this so it is always resolvable, and so the failure mode of a host that
/// forgets to plug in a real approver is "the write did not happen" rather than "the agent wrote to
/// the wrong directory unattended". A host replaces it by registering its own
/// <see cref="IToolApprover"/> after <see cref="ServiceCollectionExtensions.AddSharpCoreAgent(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{SharpCoreAgentOptions})"/>.
/// </remarks>
public sealed class DenyAllApprover : IToolApprover
{
    /// <summary>Gets the shared instance.</summary>
    public static DenyAllApprover Instance { get; } = new();

    private DenyAllApprover()
    {
    }

    /// <inheritdoc />
    public ValueTask<ApprovalOutcome> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ApprovalOutcome.Denied);
}
