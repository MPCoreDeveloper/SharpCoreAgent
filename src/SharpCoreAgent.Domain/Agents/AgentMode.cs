namespace SharpCoreAgent.Domain.Agents;

/// <summary>
/// What a turn is allowed to do: change the workspace, or only look at it.
/// </summary>
/// <remarks>
/// The mode is separate from <see cref="ApprovalMode"/> on purpose. Approval answers "may this specific
/// destructive call run?", while the mode answers "should a destructive call exist at all?". A plan turn
/// hides every destructive tool from the model and denies one if the model names it anyway, so the
/// transcript contains a plan the developer can read instead of a half-applied edit nobody approved.
/// </remarks>
public enum AgentMode
{
    /// <summary>The model may use every registered tool, subject to approval.</summary>
    Act,

    /// <summary>The model sees only read-only tools and is asked for a plan rather than an edit.</summary>
    Plan,
}
