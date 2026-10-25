using System.Net;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Chat;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The OpenAI-compatible transport: request mapping, SSE decoding, tool-call assembly and error classification.
/// </summary>
/// <remarks>
/// Every test drives the real transport over a stub handler, so the code under test is the code that ships:
/// the only fake is the socket. That is deliberate -- the branches worth covering here are the protocol ones
/// (a frame that is not JSON, an error mid-stream, a rate limit with a <c>Retry-After</c>), and a mocked
/// transport would cover the tests instead of the transport.
/// </remarks>
public sealed class OpenAiCompatibleTransportTests
{
    private const string Key = "sk-test-secret";
    private const string CredentialName = "DEEPSEEK_API_KEY";

    [Fact]
    public async Task StreamsTextDeltasAndTheCompletedResponse()
    {
        var handler = new StubHandler(_ => Sse(
            """{"id":"req-1","choices":[{"delta":{"content":"Hel"}}]}""",
            """{"id":"req-1","choices":[{"delta":{"content":"lo"},"finish_reason":"stop"}]}""",
            """{"usage":{"prompt_tokens":11,"completion_tokens":2}}""",
            "[DONE]"));

        var events = await ReadAsync(Transport(handler));

        Assert.Equal(["Hel", "lo"], events.OfType<TextDelta>().Select(delta => delta.Text));
        var completed = Assert.IsType<StreamCompleted>(events[^1]);
        Assert.Equal("Hello", completed.Response.Message.Content);
        Assert.Equal(FinishReason.Stop, completed.Response.FinishReason);
        Assert.Equal(11, completed.Response.Usage.InputTokens ?? 0);
        Assert.Equal(2, completed.Response.Usage.OutputTokens ?? 0);
    }

    [Fact]
    public async Task ReasoningArrivesBeforeTheAnswer()
    {
        var handler = new StubHandler(_ => Sse(
            """{"choices":[{"delta":{"reasoning_content":"thinking","content":"answer"}}]}""",
            "[DONE]"));

        var events = await ReadAsync(Transport(handler));

        Assert.Collection(
            events,
            first => Assert.Equal("thinking", Assert.IsType<ThinkingDelta>(first).Text),
            second => Assert.Equal("answer", Assert.IsType<TextDelta>(second).Text),
            completed => Assert.IsType<StreamCompleted>(completed));
    }

    [Fact]
    public async Task TheReasoningSpellingWithoutContentSuffixIsAlsoUnderstood()
    {
        var handler = new StubHandler(_ => Sse(
            """{"choices":[{"delta":{"reasoning":"hmm"}}]}""",
            "[DONE]"));

        var events = await ReadAsync(Transport(handler));

        Assert.Equal("hmm", Assert.IsType<ThinkingDelta>(events[0]).Text);
    }

    [Fact]
    public async Task ToolCallFragmentsAreAssembledIntoOneCall()
    {
        var handler = new StubHandler(_ => Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.txt\"}"}}]},"finish_reason":"tool_calls"}]}""",
            "[DONE]"));

        var events = await ReadAsync(Transport(handler));

        var completed = Assert.IsType<StreamCompleted>(events[^1]);
        Assert.Equal(FinishReason.ToolCalls, completed.Response.FinishReason);
        var call = Assert.Single(completed.Response.Message.ToolCalls);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("""{"path":"a.txt"}""", call.ArgumentsJson);
    }

    [Fact]
    public async Task OneMalformedFrameIsSkippedWhileTheTurnContinues()
    {
        var handler = new StubHandler(_ => Sse(
            "this is not json",
            """{"choices":[{"delta":{"content":"fine"}}]}""",
            "[DONE]"));

        var events = await ReadAsync(Transport(handler));

        Assert.Equal("fine", Assert.IsType<TextDelta>(events[0]).Text);
        Assert.IsType<StreamCompleted>(events[^1]);
    }

    [Fact]
    public async Task AStreamOfOnlyMalformedFramesReportsThem()
    {
        var handler = new StubHandler(_ => SseRaw("data: not json\n\ndata: also not json\n\n"));

        var failure = await Assert.ThrowsAsync<ModelException>(() => ReadAsync(Transport(handler)));

        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorFrameEndsTheStreamAndRedactsTheCredential()
    {
        var handler = new StubHandler(_ => Sse(
            """{"choices":[{"delta":{"content":"start"}}]}""",
            "{\"error\":{\"message\":\"rejected key " + Key + "\"}}",
            "[DONE]"));

        var failure = await Assert.ThrowsAsync<ModelException>(() => ReadAsync(Transport(handler)));

        Assert.Contains("reported an error mid-stream", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamThatEndsBeforeAnyEventIsAFailure()
    {
        var handler = new StubHandler(_ => SseRaw(string.Empty));

        var failure = await Assert.ThrowsAsync<ModelException>(() => ReadAsync(Transport(handler)));

        Assert.Contains("without sending a single event", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsyncReturnsTheTerminalResponse()
    {
        var handler = new StubHandler(_ => Sse(
            """{"choices":[{"delta":{"content":"whole"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""",
            "[DONE]"));

        var response = await Transport(handler)
            .CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("whole", response.Text);
        Assert.Equal(FinishReason.Length, response.FinishReason);
    }

    [Theory]
    [InlineData("stop", FinishReason.Stop)]
    [InlineData("length", FinishReason.Length)]
    [InlineData("tool_calls", FinishReason.ToolCalls)]
    [InlineData("function_call", FinishReason.ToolCalls)]
    [InlineData("content_filter", FinishReason.ContentFilter)]
    [InlineData("something-new", FinishReason.Unknown)]
    public async Task EveryFinishReasonTheWireCanCarryIsMapped(string wire, FinishReason expected)
    {
        var handler = new StubHandler(_ => Sse(
            $$"""{"choices":[{"delta":{"content":"x"},"finish_reason":"{{wire}}"}]}""",
            "[DONE]"));

        var response = await Transport(handler).CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.FinishReason);
    }

    [Fact]
    public async Task CommentAndEventLinesAreSkipped()
    {
        var handler = new StubHandler(_ => SseRaw(
            ":keep-alive\nevent: message\ndata:{\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n\ndata: [DONE]\n\n"));

        var events = await ReadAsync(Transport(handler));

        Assert.Equal(["a", "b"], events.OfType<TextDelta>().Select(delta => delta.Text));
    }

    [Fact]
    public async Task AMissingCredentialIsReportedByNameOnly()
    {
        var handler = new StubHandler(_ => Sse("[DONE]"));

        var failure = await Assert.ThrowsAsync<ModelCredentialException>(
            () => ReadAsync(Transport(handler, new FixedCredentials((CredentialName, null)))));

        Assert.Equal(CredentialName, failure.ConfigurationKey);
    }

    [Fact]
    public async Task TheSecondCredentialNameIsUsedWhenTheFirstIsUnset()
    {
        var handler = new StubHandler(_ => Sse("[DONE]"));
        var transport = new OpenAiCompatibleTransport(
            new HttpClient(handler),
            Descriptor,
            Endpoint,
            ["FIRST_API_KEY", "SECOND_API_KEY"],
            new FixedCredentials(("SECOND_API_KEY", "second-value")));

        await Assert.ThrowsAsync<ModelException>(() => ReadAsync(transport));

        Assert.Equal("Bearer second-value", handler.LastRequest?.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ARejectedRequestBecomesANonTransientRequestException()
    {
        var handler = new StubHandler(_ =>
        {
            var response = Json(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid key"}}""");
            response.Headers.TryAddWithoutValidation("x-request-id", "req-9");
            return response;
        });

        var failure = await Assert.ThrowsAsync<ModelRequestException>(() => ReadAsync(Transport(handler)));

        Assert.Equal(401, failure.StatusCode);
        Assert.False(failure.IsTransient);
        Assert.Contains("deepseek:deepseek-chat returned HTTP 401", failure.Message, StringComparison.Ordinal);
        Assert.Contains("invalid key", failure.Message, StringComparison.Ordinal);
        Assert.Contains("(request id req-9)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARateLimitIsTransientAndKeepsTheRequestedDelay()
    {
        var handler = new StubHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return response;
        });

        var failure = await Assert.ThrowsAsync<ModelRequestException>(() => ReadAsync(Transport(handler)));

        Assert.True(failure.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(3), failure.RetryAfter);
    }

    [Fact]
    public async Task AServerErrorIsTransient()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadGateway, "gateway exploded"));

        var failure = await Assert.ThrowsAsync<ModelRequestException>(() => ReadAsync(Transport(handler)));

        Assert.True(failure.IsTransient);
        Assert.Contains("gateway exploded", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorBodyLargerThanTheLimitIsNotRead()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, new string('x', 70_000)));

        var failure = await Assert.ThrowsAsync<ModelRequestException>(() => ReadAsync(Transport(handler)));

        Assert.Contains("was not read (limit 65536 bytes)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverlongErrorDetailIsTruncated()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, new string('y', 600)));

        var failure = await Assert.ThrowsAsync<ModelRequestException>(() => ReadAsync(Transport(handler)));

        Assert.EndsWith("…", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length < 700, failure.Message);
    }

    [Fact]
    public async Task AConnectionFailureIsTransient()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("name resolution failed"));

        var failure = await Assert.ThrowsAsync<ModelException>(() => ReadAsync(Transport(handler)));

        Assert.True(failure.IsTransient);
        Assert.Contains("could not be reached", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRequestCarriesTheModelTheTranscriptAndTheTools()
    {
        var handler = new StubHandler(_ => Sse("[DONE]"));
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("be brief"),
            ChatMessage.User("read a.txt"),
            ChatMessage.Assistant(string.Empty, [new ToolCall("call-1", "read_file", """{"path":"a.txt"}""")]),
            ChatMessage.FromToolResult(ToolResult.Failure("call-1", "read_file", "no such file")),
        };
        var tools = new List<ToolDefinition>
        {
            new("read_file", "Reads a file.", """{"type":"object"}"""),
        };

        await Assert.ThrowsAsync<ModelException>(
            () => ReadAsync(Transport(handler), Request(messages, tools, maxOutputTokens: 512, temperature: 0.35)));

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;

        Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
        Assert.Equal(0.35, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        var wire = root.GetProperty("messages");
        Assert.Equal(4, wire.GetArrayLength());
        Assert.Equal("system", wire[0].GetProperty("role").GetString());
        Assert.Equal("user", wire[1].GetProperty("role").GetString());
        Assert.Equal("assistant", wire[2].GetProperty("role").GetString());
        Assert.Equal("read_file", wire[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", wire[3].GetProperty("role").GetString());
        Assert.Equal("call-1", wire[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("no such file", wire[3].GetProperty("content").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("Bearer " + Key, handler.LastRequest?.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ARequestWithoutToolsSendsNoToolArray()
    {
        var handler = new StubHandler(_ => Sse("[DONE]"));

        await Assert.ThrowsAsync<ModelException>(() => ReadAsync(Transport(handler), Request(tools: [])));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public void DescribeRequestNamesTheEndpointWithoutTheCredential()
    {
        var transport = Transport(new StubHandler(_ => Sse("[DONE]")));

        Assert.Equal("POST api.example.test/v1/chat/completions", transport.DescribeRequest());
        Assert.Equal("deepseek:deepseek-chat", transport.Model.QualifiedName);
        Assert.Equal(Endpoint, transport.Endpoint);
    }

    [Fact]
    public void ARelativeEndpointIsRejected()
    {
        var failure = Assert.Throws<ArgumentException>(() => new OpenAiCompatibleTransport(
            new HttpClient(new StubHandler(_ => Sse("[DONE]"))),
            Descriptor,
            new Uri("/v1/chat/completions", UriKind.Relative),
            [CredentialName],
            new FixedCredentials((CredentialName, Key))));

        Assert.Contains("must be absolute", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyCredentialListIsRejected()
    {
        var failure = Assert.Throws<ArgumentException>(() => new OpenAiCompatibleTransport(
            new HttpClient(new StubHandler(_ => Sse("[DONE]"))),
            Descriptor,
            Endpoint,
            [],
            new FixedCredentials()));

        Assert.Contains("credential", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelDescriptor Descriptor => new("deepseek", "deepseek-chat", "DeepSeek Chat");

    private static Uri Endpoint => new("https://api.example.test/v1/chat/completions");

    private static OpenAiCompatibleTransport Transport(StubHandler handler, ICredentialStore? credentials = null) =>
        new(
            new HttpClient(handler),
            Descriptor,
            Endpoint,
            [CredentialName],
            credentials ?? new FixedCredentials((CredentialName, Key)));

    private static ChatRequest Request(
        IReadOnlyList<ChatMessage>? messages = null,
        IReadOnlyList<ToolDefinition>? tools = null,
        int? maxOutputTokens = 256,
        double temperature = 0.1) =>
        new(
            Descriptor,
            messages ?? [ChatMessage.System("be brief"), ChatMessage.User("hi")],
            tools ?? [],
            temperature,
            maxOutputTokens);

    /// <summary>Reads the whole stream, so a failure surfaces here rather than mid-assertion.</summary>
    /// <param name="transport">The transport under test.</param>
    /// <param name="request">The request, or a default one.</param>
    /// <returns>Every event the transport reported.</returns>
    private static async Task<List<ChatStreamEvent>> ReadAsync(
        OpenAiCompatibleTransport transport,
        ChatRequest? request = null)
    {
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in transport
            .StreamAsync(request ?? Request(), TestContext.Current.CancellationToken)
            .ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    /// <summary>Builds a <c>text/event-stream</c> answer with one <c>data:</c> line per payload.</summary>
    /// <param name="payloads">The payloads, in order.</param>
    /// <returns>The response.</returns>
    private static HttpResponseMessage Sse(params string[] payloads)
    {
        var body = new StringBuilder();

        foreach (var payload in payloads)
        {
            body.Append("data: ").Append(payload).Append("\n\n");
        }

        return SseRaw(body.ToString());
    }

    private static HttpResponseMessage SseRaw(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Answers every request with whatever the test decides, and remembers what was sent.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return respond(request);
        }
    }

    /// <summary>Supplies credential values from a table, so a test can decide what is configured.</summary>
    private sealed class FixedCredentials : ICredentialStore
    {
        private readonly Dictionary<string, string?> _values;

        public FixedCredentials(params (string Name, string? Value)[] values) =>
            _values = values.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

        public string? Find(string name) => _values.GetValueOrDefault(name);

        public string GetRequired(string name) => Find(name) ?? throw new ModelCredentialException(name);
    }
}

