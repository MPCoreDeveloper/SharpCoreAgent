namespace SharpCoreAgent.Domain.Agents;

/// <summary>
/// Receives the agent's progress as it happens.
/// </summary>
/// <remarks>
/// The agent awaits every callback, so a slow observer slows the turn. That is deliberate: a console
/// that cannot keep up should throttle the loop rather than let unbounded text queue up behind it.
/// </remarks>
public interface IAgentObserver
{
    /// <summary>
    /// Handles one event.
    /// </summary>
    /// <param name="agentEvent">The event.</param>
    /// <param name="cancellationToken">Cancels the callback itself.</param>
    /// <returns>A task that completes when the event has been rendered.</returns>
    ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken);
}

/// <summary>
/// An observer that discards every event. Useful for tests and for <c>--quiet</c> runs.
/// </summary>
public sealed class NullAgentObserver : IAgentObserver
{
    /// <summary>Gets the shared instance.</summary>
    public static NullAgentObserver Instance { get; } = new();

    private NullAgentObserver()
    {
    }

    /// <inheritdoc />
    public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
