using System.Runtime.CompilerServices;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Chat;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The fallback rule: a refusal that says something about the provider is replayed once at the model the
/// developer named, and nothing else is.
/// </summary>
/// <remarks>
/// Every test runs against the shipping decorator with scripted adapters, so the assertions are about the
/// decision rather than about HTTP: which attempts happened, what the replay carried, and what the caller
/// saw before and after it.
/// </remarks>
public sealed class FallbackChatModelTests
{
    private static readonly ModelDescriptor Primary = new("demo", "demo-primary", "Primary");

    private static readonly ModelDescriptor Secondary = new("grok", "grok-4.7", "Grok");

    private static readonly ChatRequest Request = new(
        Primary,
        [ChatMessage.User("hello")],
        [],
        Temperature: 0.7,
        MaxOutputTokens: 512);

    private static readonly ChatResponse Answer = new(
        ChatMessage.Assistant("from the fallback"),
        FinishReason.Stop,
        new TokenUsage(12, 34));

    [Fact]
    public async Task AnUnauthorizedPrimaryIsReplayedAtTheFallback()
    {
        var primary = new ScriptedModel(Primary, [Refuse(401, "invalid key")]);
        var fallback = new ScriptedModel(Secondary, [new TextDelta("from the fallback"), new StreamCompleted(Answer)]);

        var events = await CollectAsync(new FallbackChatModel(primary, fallback));

        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
        Assert.Equal(["from the fallback"], events.OfType<TextDelta>().Select(delta => delta.Text));
        Assert.Single(events.OfType<StreamCompleted>());

        var notice = Assert.Single(events.OfType<FallbackUsed>());
        Assert.Equal("demo:demo-primary", notice.PrimaryModelId);
        Assert.Equal("grok:grok-4.7", notice.FallbackModelId);
        Assert.Equal(401, notice.StatusCode);
        Assert.Contains("invalid key", notice.Reason, StringComparison.Ordinal);
        Assert.Equal("demo:demo-primary failed with HTTP 401; grok:grok-4.7 answered", notice.ToString());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task AServerFailureIsReplayedAtTheFallback(int status)
    {
        var primary = new ScriptedModel(Primary, [Refuse(status, "upstream is having a minute")]);
        var fallback = new ScriptedModel(Secondary, [new StreamCompleted(Answer)]);

        var events = await CollectAsync(new FallbackChatModel(primary, fallback));

        Assert.Equal(1, fallback.Calls);
        Assert.Equal(status, Assert.Single(events.OfType<FallbackUsed>()).StatusCode);
        Assert.Single(events.OfType<StreamCompleted>());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    [InlineData(429)]
    public async Task ARequestTheFallbackWouldAlsoRefuseIsNotReplayed(int status)
    {
        var fallback = new ScriptedModel(Secondary, [new StreamCompleted(Answer)]);
        var model = new FallbackChatModel(new ScriptedModel(Primary, [Refuse(status, "the body is wrong")]), fallback);

        var error = await Assert.ThrowsAsync<ModelRequestException>(async () => await CollectAsync(model));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task ATransientFailureWithoutAStatusIsNotReplayed()
    {
        // Only 401 and 5xx move the turn: a dropped connection is the retry decorator's problem, and it
        // gets another go at the same provider before anyone else is asked.
        var fallback = new ScriptedModel(Secondary, [new StreamCompleted(Answer)]);
        var model = new FallbackChatModel(
            new ScriptedModel(Primary, [new ModelException("the connection failed", isTransient: true)]),
            fallback);

        await Assert.ThrowsAsync<ModelException>(async () => await CollectAsync(model));

        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task AFailureAfterContentIsSurfacedRatherThanReplayed()
    {
        var fallback = new ScriptedModel(Secondary, [new StreamCompleted(Answer)]);
        var model = new FallbackChatModel(
            new ScriptedModel(Primary, [new TextDelta("half an ans"), Refuse(503, "gone")]),
            fallback);

        var events = new List<ChatStreamEvent>();
        await Assert.ThrowsAsync<ModelRequestException>(async () =>
        {
            await foreach (var streamEvent in model.StreamAsync(Request, TestContext.Current.CancellationToken))
            {
                events.Add(streamEvent);
            }
        });

        Assert.Equal(["half an ans"], events.OfType<TextDelta>().Select(delta => delta.Text));
        Assert.Empty(events.OfType<FallbackUsed>());
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task TheReplayCarriesTheFallbackModelAndTheRequestsOwnSettings()
    {
        var fallback = new ScriptedModel(Secondary, [new StreamCompleted(Answer)]);

        await CollectAsync(new FallbackChatModel(new ScriptedModel(Primary, [Refuse(401, "expired")]), fallback));

        var replay = Assert.IsType<ChatRequest>(fallback.LastRequest);
        Assert.Equal(Secondary, replay.Model);
        Assert.Equal(Request.Messages, replay.Messages);
        Assert.Equal(Request.Tools, replay.Tools);
        Assert.Equal(Request.Temperature, replay.Temperature);
        Assert.Equal(Request.MaxOutputTokens, replay.MaxOutputTokens);
    }

    [Fact]
    public async Task AFallbackThatAlsoFailsStillNamesBothAttempts()
    {
        var model = new FallbackChatModel(
            new ScriptedModel(Primary, [Refuse(503, "busy")]),
            new ScriptedModel(Secondary, [Refuse(500, "also busy")]));

        var events = new List<ChatStreamEvent>();
        var error = await Assert.ThrowsAsync<ModelRequestException>(async () =>
        {
            await foreach (var streamEvent in model.StreamAsync(Request, TestContext.Current.CancellationToken))
            {
                events.Add(streamEvent);
            }
        });

        Assert.Equal(500, error.StatusCode);
        var notice = Assert.Single(events.OfType<FallbackUsed>());
        Assert.Equal("demo:demo-primary", notice.PrimaryModelId);
        Assert.Equal("grok:grok-4.7", notice.FallbackModelId);
    }

    [Fact]
    public async Task APrimaryThatAnswersIsNeverReplayed()
    {
        var fallback = new ScriptedModel(Secondary, [new StreamCompleted(Answer)]);
        var model = new FallbackChatModel(
            new ScriptedModel(Primary, [new TextDelta("from the primary"), new StreamCompleted(Answer)]),
            fallback);

        var events = await CollectAsync(model);

        Assert.Equal(0, fallback.Calls);
        Assert.Empty(events.OfType<FallbackUsed>());
        Assert.Equal("demo:demo-primary", model.Model.QualifiedName);
        Assert.Equal("grok:grok-4.7", model.FallbackModel.QualifiedName);
    }

    [Fact]
    public void ThePairIsCheckedAtConstruction()
    {
        var primary = new ScriptedModel(Primary, []);
        var fallback = new ScriptedModel(Secondary, []);

        Assert.Throws<ArgumentNullException>(() => new FallbackChatModel(null!, fallback));
        Assert.Throws<ArgumentNullException>(() => new FallbackChatModel(primary, null!));
        Assert.Throws<ArgumentException>(() => new FallbackChatModel(primary, new ScriptedModel(Primary, [])));
        Assert.Throws<ArgumentNullException>(
            () => new FallbackChatModel(primary, fallback).StreamAsync(null!, TestContext.Current.CancellationToken));
    }

    private static ModelException Refuse(int status, string detail) =>
        new ModelRequestException(Primary.Provider, Primary.Id, status, detail, isTransient: status >= 500);

    private static async Task<List<ChatStreamEvent>> CollectAsync(FallbackChatModel model)
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
    /// order. The last behaviour repeats, so a test never has to predict how many attempts happen.
    /// </summary>
    private sealed class ScriptedModel(ModelDescriptor model, params object[][] behaviours) : IStreamingChatModel
    {
        private int _calls;

        public int Calls => _calls;

        public ChatRequest? LastRequest { get; private set; }

        public ModelDescriptor Model => model;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
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
