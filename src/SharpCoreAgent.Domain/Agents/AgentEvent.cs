namespace SharpCoreAgent.Domain.Agents;

/// <summary>
/// Something the agent wants the UI to show while a turn runs.
/// </summary>
/// <remarks>
/// Events are the only channel from the loop to the console. The loop never writes to a stream, which
/// is what lets the same loop drive a test observer, a JSON renderer and an interactive terminal.
/// </remarks>
public abstract record AgentEvent;

/// <summary>
/// A turn has begun against a specific model.
/// </summary>
/// <param name="ModelId">The <c>provider:model</c> name.</param>
public sealed record TurnStarted(string ModelId) : AgentEvent;

/// <summary>
/// The model produced reasoning text.
/// </summary>
/// <param name="Text">The increment.</param>
public sealed record AgentThinkingDelta(string Text) : AgentEvent;

/// <summary>
/// The model produced answer text.
/// </summary>
/// <param name="Text">The increment.</param>
public sealed record AgentTextDelta(string Text) : AgentEvent;

/// <summary>
/// The model asked for a tool, before approval and before execution.
/// </summary>
/// <param name="CallId">The call identifier.</param>
/// <param name="ToolName">The requested tool.</param>
/// <param name="ArgumentsJson">The raw arguments, shown to the developer when approval is required.</param>
public sealed record ToolRequested(string CallId, string ToolName, string ArgumentsJson) : AgentEvent;

/// <summary>
/// A tool invocation finished, one way or another.
/// </summary>
/// <param name="CallId">The call identifier.</param>
/// <param name="ToolName">The tool.</param>
/// <param name="IsError">Whether the result is a failure or a refusal.</param>
/// <param name="Summary">One line describing the outcome.</param>
public sealed record ToolFinished(string CallId, string ToolName, bool IsError, string Summary) : AgentEvent;

/// <summary>
/// The turn is over; the report is final.
/// </summary>
/// <param name="Report">The report also returned to the caller.</param>
public sealed record TurnFinished(AgentReport Report) : AgentEvent;
