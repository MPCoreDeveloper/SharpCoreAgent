using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Renders the agent's events as frames on a hub, for the page to draw.
/// </summary>
/// <remarks>
/// The observer holds no per-turn state, unlike <see cref="ConsoleObserver"/>: every event maps to one frame and one
/// frame only, so a turn and the next turn stream to a page that reconnected between them with nothing to reset. The
/// loop awaits each callback, and the hub never blocks, so a page that is not reading cannot slow the turn.
/// </remarks>
/// <param name="hub">Where frames go.</param>
internal sealed class WebObserver(ServeHub hub) : IAgentObserver
{
    private readonly ServeHub _hub = hub;

    /// <inheritdoc />
    public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        string? frame = agentEvent switch
        {
            TurnStarted started => ServeEvents.TurnStarted(started.ModelId),
            AgentThinkingDelta thinking => ServeEvents.Thinking(thinking.Text),
            AgentTextDelta text => ServeEvents.Text(text.Text),
            ToolRequested requested => ServeEvents.ToolStarted(requested.CallId, requested.ToolName, requested.ArgumentsJson),
            ToolOutputDelta output => ServeEvents.ToolOutput(output.CallId, output.ToolName, output.Text),
            ToolFinished finished => ServeEvents.ToolFinished(finished.CallId, finished.ToolName, !finished.IsError, finished.Summary),
            ContextCompacted compacted => ServeEvents.ContextCompacted(compacted.Dropped, compacted.Kept),
            FallbackAttempted fallback => ServeEvents.Fallback(fallback.Fallback),
            TurnFinished done => ServeEvents.Report(done.Report),
            _ => null,
        };

        if (frame is not null)
        {
            _hub.Publish(frame);
        }

        return ValueTask.CompletedTask;
    }
}
