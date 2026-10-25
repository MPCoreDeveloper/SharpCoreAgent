namespace SharpCoreAgent.Domain.Model;

/// <summary>
/// One increment of a streamed response.
/// </summary>
/// <remarks>
/// The stream is a closed set of three shapes: text, optional reasoning, and exactly one terminal
/// <see cref="StreamCompleted"/>. Adapters that have nothing to add yet emit nothing rather than an
/// empty delta, so the consumer never has to distinguish "no content" from "empty content" for
/// rendering purposes.
/// </remarks>
public abstract record ChatStreamEvent;

/// <summary>
/// A piece of assistant text, already decoded.
/// </summary>
/// <param name="Text">The increment; may be a single token or a whole sentence.</param>
public sealed record TextDelta(string Text) : ChatStreamEvent;

/// <summary>
/// A piece of reasoning text a provider streams separately from the answer.
/// </summary>
/// <param name="Text">The increment.</param>
public sealed record ThinkingDelta(string Text) : ChatStreamEvent;

/// <summary>
/// The terminal event: the response is complete and every tool call has been assembled.
/// </summary>
/// <param name="Response">The finished response.</param>
public sealed record StreamCompleted(ChatResponse Response) : ChatStreamEvent;
