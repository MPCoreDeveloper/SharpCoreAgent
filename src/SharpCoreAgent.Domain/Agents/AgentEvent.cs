using SharpCoreAgent.Domain.Model;

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
/// The turn moved to the fallback model because the primary returned 401 or 5xx.
/// </summary>
/// <param name="Fallback">Which attempt failed, why, and which model serves the request instead.</param>
/// <remarks>
/// Emitted as it happens rather than in the closing summary: the answer the developer is about to
/// read comes from a different provider than the one the prompt named, and that is worth a line before
/// the text arrives.
/// </remarks>
public sealed record FallbackAttempted(FallbackUsed Fallback) : AgentEvent;

/// <summary>
/// The oldest part of the transcript was dropped so the next request stays inside the context window.
/// </summary>
/// <param name="Dropped">How many messages were replaced by the elision note.</param>
/// <param name="Kept">How many of the most recent messages were kept.</param>
/// <remarks>
/// Emitted before the request that needed the room, never after: a long session that silently forgets the
/// beginning of the conversation answers a question about “the file we just edited” with confidence and
/// no memory, and the developer deserves a line saying why. The elision note the model receives says the
/// same thing, so asking again is a real remedy.
/// </remarks>
public sealed record ContextCompacted(int Dropped, int Kept) : AgentEvent;

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
/// A running tool produced output, before it has finished.
/// </summary>
/// <param name="CallId">The call identifier.</param>
/// <param name="ToolName">The tool that is speaking.</param>
/// <param name="Text">The increment; it may end mid-line.</param>
/// <remarks>
/// Only tools that can take a long time emit this — a shell command while it runs, so a developer
/// watches the build instead of waiting for it. The event is additive: a tool's returned text is
/// complete on its own, and an observer that ignores this event loses nothing but timing.
/// </remarks>
public sealed record ToolOutputDelta(string CallId, string ToolName, string Text) : AgentEvent;

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
