using System.Runtime.CompilerServices;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Chat;

/// <summary>
/// Replays one request at the developer's named fallback when the primary refuses it, and never after
/// the answer has started.
/// </summary>
/// <remarks>
/// The rule is narrower than <see cref="RetryingChatModel"/>'s on purpose. A malformed request (400)
/// would fail the same way at the fallback, so it is not replayed; a 401 or a 5xx says something about
/// the provider rather than the request, which is exactly the case a second provider can answer. The
/// decorator wraps the retry decorator rather than the other way round, so the primary is retried in
/// place first and only its final refusal becomes a fallback.
/// <para>
/// Nothing here knows a credential, and the reason it carries is the exception's own message, which the
/// adapters already sanitise.
/// </para>
/// </remarks>
public sealed class FallbackChatModel : IStreamingChatModel
{
    private readonly IStreamingChatModel _primary;
    private readonly IStreamingChatModel _fallback;

    /// <summary>
    /// Pairs a primary with the model that answers when the primary cannot.
    /// </summary>
    /// <param name="primary">The model the developer asked for, normally already wrapped in retry.</param>
    /// <param name="fallback">The model to replay the request at, also normally retry-wrapped.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Both arguments name the same model.</exception>
    public FallbackChatModel(IStreamingChatModel primary, IStreamingChatModel fallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);

        if (string.Equals(primary.Model.QualifiedName, fallback.Model.QualifiedName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A fallback must name a different model than the primary; a replay there would repeat the same request.",
                nameof(fallback));
        }

        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>Gets the model the developer asked for.</summary>
    public ModelDescriptor Model => _primary.Model;

    /// <summary>Gets the model a refused request is replayed at.</summary>
    public ModelDescriptor FallbackModel => _fallback.Model;

    /// <inheritdoc />
    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return StreamCoreAsync(request, cancellationToken);
    }

    /// <summary>
    /// Streams the primary, and replays the request at the fallback when it refuses before saying anything.
    /// </summary>
    /// <param name="request">The request to send; already checked for null.</param>
    /// <param name="cancellationToken">Aborts the stream that is running.</param>
    /// <returns>The primary's events, or a notice and the fallback's events.</returns>
    /// <remarks>
    /// The enumerator is driven by hand for the same reason <see cref="RetryingChatModel"/> does it: a
    /// <c>yield return</c> inside a <c>try</c> with a <c>catch</c> is not allowed, and the decision to
    /// fall back has to be made where the exception is caught.
    /// </remarks>
    private async IAsyncEnumerable<ChatStreamEvent> StreamCoreAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var yieldedContent = false;
        ModelRequestException? refusal = null;

        await using (var source = _primary.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                ChatStreamEvent? next = null;

                try
                {
                    if (!await source.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    next = source.Current;
                }
                catch (ModelRequestException ex) when (!yieldedContent && IsFallbackStatus(ex.StatusCode))
                {
                    refusal = ex;
                    break;
                }

                yieldedContent |= next is TextDelta or ThinkingDelta;
                yield return next;
            }
        }

        if (refusal is null)
        {
            yield break;
        }

        // Announced before the replay so a report can name both attempts even when the fallback also
        // fails: the failure that ends the turn then arrives with its own explanation beside it.
        yield return new FallbackUsed(
            _primary.Model.QualifiedName,
            refusal.StatusCode,
            refusal.Message,
            _fallback.Model.QualifiedName);

        // The identifier is the one field that has to change: the rest of the request is the request
        // the developer's prompt produced, unchanged.
        var replay = request with { Model = _fallback.Model };

        await foreach (var streamEvent in _fallback.StreamAsync(replay, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return streamEvent;
        }
    }

    /// <summary>
    /// Decides whether a status is the provider's problem rather than the request's.
    /// </summary>
    /// <param name="statusCode">The status the primary returned.</param>
    /// <returns><see langword="true"/> for 401 and every 5xx.</returns>
    /// <remarks>
    /// 401 means this credential is not accepted here, and the request itself is well-formed, so the
    /// replay is worth one attempt. 4xx other than 401 would meet the same answer twice.
    /// </remarks>
    private static bool IsFallbackStatus(int statusCode) => statusCode == 401 || statusCode >= 500;
}
