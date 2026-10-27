using System.Globalization;

namespace SharpCoreAgent.Domain.Model;

/// <summary>
/// One increment of a streamed response.
/// </summary>
/// <remarks>
/// The stream is a closed set of four shapes: text, optional reasoning, at most one
/// <see cref="FallbackUsed"/> notice, and exactly one terminal <see cref="StreamCompleted"/>. Adapters
/// that have nothing to add yet emit nothing rather than an empty delta, so the consumer never has to
/// distinguish "no content" from "empty content" for rendering purposes.
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
/// The request was moved to the developer's named fallback because the primary refused it.
/// </summary>
/// <param name="PrimaryModelId">The model that failed, as <c>provider:model</c>.</param>
/// <param name="StatusCode">The HTTP status that caused the move: 401, or a 5xx.</param>
/// <param name="Reason">What the failed attempt said, with any credential already redacted.</param>
/// <param name="FallbackModelId">The model that serves the request instead.</param>
/// <remarks>
/// The notice arrives before the fallback's first increment, so a consumer sees it only when the
/// primary produced nothing: a stream that has already yielded content is never replayed. It is the
/// only event in the protocol that is not about content, and it exists so that a report can name both
/// attempts rather than only the one that answered.
/// </remarks>
public sealed record FallbackUsed(
    string PrimaryModelId,
    int StatusCode,
    string Reason,
    string FallbackModelId) : ChatStreamEvent
{
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{PrimaryModelId} failed with HTTP {StatusCode}; {FallbackModelId} answered");
}

/// <summary>
/// The terminal event: the response is complete and every tool call has been assembled.
/// </summary>
/// <param name="Response">The finished response.</param>
public sealed record StreamCompleted(ChatResponse Response) : ChatStreamEvent;
