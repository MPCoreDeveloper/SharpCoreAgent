using System.Runtime.CompilerServices;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Chat;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The one rule the retry decorator exists to enforce: a transient failure is retried only while nothing
/// has been shown to the developer, and the attempt count is bounded. Every test runs against the
/// shipping decorator with a scripted adapter, and the policy is squeezed to a one-millisecond delay so
/// the arithmetic is real without the wait.
/// </summary>
public sealed class RetryingChatModelTests
{
    private static readonly ModelDescriptor Descriptor = new("demo", "demo-scripted", "Demo");

    private static readonly ChatRequest Request = new(Descriptor, [ChatMessage.User("hello")], []);

    private static readonly ChatResponse Answer = new(ChatMessage.Assistant("done"), FinishReason.Stop, TokenUsage.Unknown);

    [Fact]
    public async Task ATransientFailureBeforeAnyContentIsRetried()
    {
        var model = new RetryingChatModel(
            new ScriptedModel([Fail("rate limited")], [new TextDelta("done"), new StreamCompleted(Answer)]),
            FastPolicy(),
            TimeProvider.System);

        var events = await CollectAsync(model);

        Assert.Equal(["done"], events.OfType<TextDelta>().Select(delta => delta.Text));
        Assert.Single(events.OfType<StreamCompleted>());
    }

    [Fact]
    public async Task ATransientFailureAfterContentIsSurfacedRatherThanReplayed()
    {
        var model = new RetryingChatModel(
            new ScriptedModel([new TextDelta("half an ans"), Fail("connection reset")]),
            FastPolicy(),
            TimeProvider.System);

        var events = new List<ChatStreamEvent>();

        var error = await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var streamEvent in model.StreamAsync(Request, TestContext.Current.CancellationToken))
            {
                events.Add(streamEvent);
            }
        });

        Assert.True(error.IsTransient);
        Assert.Equal(["half an ans"], events.OfType<TextDelta>().Select(delta => delta.Text));
    }

    [Fact]
    public async Task APermanentFailureIsSurfacedImmediately()
    {
        var inner = new ScriptedModel([new ModelException("bad key")]);
        var model = new RetryingChatModel(inner, FastPolicy(), TimeProvider.System);

        await Assert.ThrowsAsync<ModelException>(async () => await CollectAsync(model));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task TheAttemptCapIsHonoured()
    {
        var inner = new ScriptedModel([Fail("rate limited")], [Fail("rate limited")], [Fail("rate limited")]);
        var model = new RetryingChatModel(inner, FastPolicy(maxAttempts: 3), TimeProvider.System);

        var error = await Assert.ThrowsAsync<ModelException>(async () => await CollectAsync(model));

        Assert.Contains("rate limited", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task TheCallbackSeesTheAttemptTheCapAndTheReason()
    {
        var attempts = new List<RetryAttempt>();
        var model = new RetryingChatModel(
            new ScriptedModel([Fail("rate limited")], [new StreamCompleted(Answer)]),
            FastPolicy(maxAttempts: 2),
            TimeProvider.System,
            attempts.Add);

        await CollectAsync(model);

        var attempt = Assert.Single(attempts);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(2, attempt.MaxAttempts);
        Assert.Contains("rate limited", attempt.Reason, StringComparison.Ordinal);
        Assert.Contains("retrying (2/2)", attempt.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAttemptThatNeverFailsIsNotRetried()
    {
        var inner = new ScriptedModel([new TextDelta("done"), new StreamCompleted(Answer)]);
        var model = new RetryingChatModel(inner, FastPolicy(), TimeProvider.System);

        var events = await CollectAsync(model);

        Assert.Equal(1, inner.Calls);
        Assert.Single(events.OfType<StreamCompleted>());
    }

    [Fact]
    public void NullsAreRejected()
    {
        var inner = new ScriptedModel([]);

        Assert.Throws<ArgumentNullException>(() => new RetryingChatModel(null!, FastPolicy(), TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new RetryingChatModel(inner, null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new RetryingChatModel(inner, FastPolicy(), null!));
        Assert.Throws<ArgumentNullException>(
            () => new RetryingChatModel(inner, FastPolicy(), TimeProvider.System).StreamAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThePolicyIsValidatedAtConstruction() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RetryingChatModel(new ScriptedModel([]), new RetryPolicy { MaxAttempts = 0 }, TimeProvider.System));

    private static RetryPolicy FastPolicy(int maxAttempts = 3) => new()
    {
        MaxAttempts = maxAttempts,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.FromMilliseconds(1),
        MaxJitterMilliseconds = 0,
    };

    private static ModelException Fail(string reason) => new(reason, isTransient: true);

    private static async Task<List<ChatStreamEvent>> CollectAsync(RetryingChatModel model)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in model.StreamAsync(Request, TestContext.Current.CancellationToken))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    /// <summary>
    /// Plays one scripted behaviour per call, where a behaviour is the events and exceptions to play in
    /// order. The last behaviour repeats, so a test never has to predict how many attempts the decorator
    /// makes.
    /// </summary>
    private sealed class ScriptedModel(params object[][] behaviours) : IStreamingChatModel
    {
        private int _calls;

        public int Calls => _calls;

        public ModelDescriptor Model => Descriptor;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var steps = behaviours.Length == 0 ? [] : behaviours[Math.Min(_calls++, behaviours.Length - 1)];

            foreach (var step in steps)
            {
                if (step is Exception failure)
                {
                    await Task.Yield();
                    throw failure;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return (ChatStreamEvent)step;
            }
        }
    }
}
