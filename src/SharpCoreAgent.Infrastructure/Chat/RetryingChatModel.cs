using System.Runtime.CompilerServices;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Chat;

/// <summary>
/// Adds bounded retry to any streaming model, and is the only place in the product that retries.
/// </summary>
/// <remarks>
/// The rule the decorator enforces is the one an adapter cannot enforce for itself: a transient
/// failure is retried only while nothing has been shown to the developer. Once a <see cref="TextDelta"/>
/// or <see cref="ThinkingDelta"/> has escaped, a retry would replay text that is already on screen, so
/// the failure is surfaced instead.
/// <para>
/// The clock and the jitter are injected so tests can assert the delay arithmetic without waiting for
/// it.
/// </para>
/// </remarks>
public sealed class RetryingChatModel : IStreamingChatModel
{
    private readonly IStreamingChatModel _inner;
    private readonly RetryPolicy _policy;
    private readonly TimeProvider _time;
    private readonly Action<RetryAttempt>? _onRetry;

    /// <summary>
    /// Wraps a model in the retry policy.
    /// </summary>
    /// <param name="inner">The adapter that talks to the provider.</param>
    /// <param name="policy">How patient to be; <see cref="RetryPolicy.Default"/> when omitted.</param>
    /// <param name="time">The clock used for the delay, so a test can advance it.</param>
    /// <param name="onRetry">
    /// Called before each retry, once the delay is known; the CLI prints "retrying (2/3)" from it.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public RetryingChatModel(
        IStreamingChatModel inner,
        RetryPolicy policy,
        TimeProvider time,
        Action<RetryAttempt>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(time);

        _inner = inner;
        _policy = policy.Validate();
        _time = time;
        _onRetry = onRetry;
    }

    /// <summary>Gets the model the wrapped adapter speaks to.</summary>
    public ModelDescriptor Model => _inner.Model;

    /// <summary>Gets the policy in force.</summary>
    public RetryPolicy Policy => _policy;

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var attempt = 1; ; attempt++)
        {
            RetryAttempt? retry = null;

            await using (var source = _inner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                var yieldedContent = false;

                while (true)
                {
                    try
                    {
                        if (!await source.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (ModelException ex) when (ex.IsTransient && !yieldedContent && attempt < _policy.MaxAttempts)
                    {
                        retry = new RetryAttempt(
                            attempt,
                            _policy.MaxAttempts,
                            _policy.DelayFor(attempt, ex.RetryAfter, Random.Shared.Next(0, _policy.MaxJitterMilliseconds + 1)),
                            ex.Message);
                        break;
                    }

                    // Only content matters for the "never resend" rule; the terminal event cannot have
                    // arrived before a failure, so it needs no special case.
                    yieldedContent |= source.Current is TextDelta or ThinkingDelta;
                    yield return source.Current;
                }
            }

            if (retry is null)
            {
                yield break;
            }

            _onRetry?.Invoke(retry);
            await Task.Delay(retry.Delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
