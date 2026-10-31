using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// One of the three jobs a session hands to a model.
/// </summary>
/// <remarks>
/// A role is a question about <em>which</em> model answers, not about what a turn may do: the mode
/// (<see cref="AgentMode"/>) already answers that, and the loop reads the mode from the session. Keeping the
/// role here rather than in the domain is deliberate — the model is resolved before the session exists, so
/// the mapping from a job to a model is the host's decision, and <c>Domain</c> and <c>Application</c> stay
/// exactly as provider-agnostic as principle I requires.
/// </remarks>
internal enum AgentRole
{
    /// <summary>The model a plan turn runs on: reading and reasoning, as cheap or as strong as the job needs.</summary>
    Planner,

    /// <summary>The model an act turn runs on, and the one <c>/model</c> starts from.</summary>
    Executor,

    /// <summary>The model <c>/review</c> sends its read-only turn to.</summary>
    Reviewer,
}

/// <summary>
/// The three roles, the words they are called by, and the one place a role becomes a
/// <see cref="ModelDescriptor"/>.
/// </summary>
/// <remarks>
/// A role names a model the same way <c>/model</c> and <c>--model</c> do: a <c>provider:id</c> pair, one of
/// the catalogue's aliases, or a bare identifier that keeps <c>--provider</c>. Resolution happens here and
/// nowhere else, so the flag, the settings file, the environment and the loop cannot disagree about what
/// <c>--planner grok</c> means.
/// </remarks>
internal static class AgentRoles
{
    /// <summary>Gets every role, in the order a report lists them.</summary>
    public static IReadOnlyList<AgentRole> All { get; } = [AgentRole.Planner, AgentRole.Executor, AgentRole.Reviewer];

    /// <summary>Names a role as the setting and the flag spell it.</summary>
    /// <param name="role">The role.</param>
    /// <returns><c>planner</c>, <c>executor</c> or <c>reviewer</c>.</returns>
    public static string Name(AgentRole role) => role switch
    {
        AgentRole.Planner => "planner",
        AgentRole.Executor => "executor",
        _ => "reviewer",
    };

    /// <summary>Names a role by the word the developer types, so the screen says <c>plan</c> and not <c>planner</c>.</summary>
    /// <param name="role">The role.</param>
    /// <returns><c>plan</c>, <c>act</c> or <c>review</c>.</returns>
    public static string Label(AgentRole role) => role switch
    {
        AgentRole.Planner => "plan",
        AgentRole.Executor => "act",
        _ => "review",
    };

    /// <summary>
    /// Reads the text a role was given, or <see langword="null"/> when the developer named no model for it.
    /// </summary>
    /// <param name="options">The parsed command line, which is where a role is stored as text.</param>
    /// <param name="role">The role to read.</param>
    /// <returns>The model as it was written, or <see langword="null"/>.</returns>
    public static string? Text(CliOptions options, AgentRole role) => role switch
    {
        AgentRole.Planner => options.PlannerModel,
        AgentRole.Executor => options.ExecutorModel,
        _ => options.ReviewerModel,
    };

    /// <summary>
    /// Resolves the model a role names.
    /// </summary>
    /// <param name="options">The parsed command line, for the text and the provider a bare id keeps.</param>
    /// <param name="role">The role to resolve.</param>
    /// <returns>The descriptor, or <see langword="null"/> when the role names no model.</returns>
    public static ModelDescriptor? For(CliOptions options, AgentRole role) =>
        Text(options, role) is { } text ? Resolve(text, options.Provider) : null;

    /// <summary>Answers which role a turn in this mode runs on.</summary>
    /// <param name="mode">The mode the next turn will run in.</param>
    /// <returns><see cref="AgentRole.Planner"/> for a plan turn, <see cref="AgentRole.Executor"/> otherwise.</returns>
    public static AgentRole ForMode(AgentMode mode) => mode == AgentMode.Plan ? AgentRole.Planner : AgentRole.Executor;

    /// <summary>
    /// Resolves the model a turn in this mode runs on.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="mode">The mode the turn will run in.</param>
    /// <param name="sessionModel">The model the conversation is already on, used when the role names none.</param>
    /// <returns>The role's model, or <paramref name="sessionModel"/>.</returns>
    /// <remarks>
    /// The fallback is the session's own model rather than the command line's: after <c>/model</c> the two
    /// differ, and a role nobody named must not undo a choice the developer made mid-conversation.
    /// </remarks>
    public static ModelDescriptor ForMode(CliOptions options, AgentMode mode, ModelDescriptor sessionModel) =>
        For(options, ForMode(mode)) ?? sessionModel;

    /// <summary>
    /// Lists every model this process may send a request to.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="sessionModel">The model the conversation starts on.</param>
    /// <returns>The session model, every named role model and the fallback, each once, in that order.</returns>
    /// <remarks>
    /// The credential preflight reads this: a key missing for one role is a turn that fails
    /// mid-conversation, which is exactly what checking before the first request exists to prevent.
    /// </remarks>
    public static IReadOnlyList<ModelDescriptor> RequestModels(CliOptions options, ModelDescriptor sessionModel)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessionModel);

        var models = new List<ModelDescriptor> { sessionModel };
        foreach (var role in All)
        {
            if (For(options, role) is { } model)
            {
                models.Add(model);
            }
        }

        if (options.Fallback is { } fallback)
        {
            models.Add(fallback);
        }

        return [.. models.DistinctBy(model => model.QualifiedName)];
    }

    /// <summary>
    /// Turns the text a developer wrote into the descriptor it names.
    /// </summary>
    /// <param name="text">A <c>provider:id</c> pair, an alias, or a bare model identifier.</param>
    /// <param name="provider">The provider a bare identifier keeps.</param>
    /// <returns>The descriptor.</returns>
    /// <exception cref="ArgumentException">The text names a provider this build does not serve.</exception>
    public static ModelDescriptor Resolve(string text, string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (ModelCatalog.TryParseQualified(text, out var qualified))
        {
            return qualified!;
        }

        return ModelCatalog.TryResolveAlias(text, out var aliasProvider, out var aliasModel)
            ? ModelCatalog.DescriptorFor(aliasProvider, aliasModel)
            : ModelCatalog.DescriptorFor(provider, text.Trim());
    }
}
