using System.Text.Json;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// Sampling policy at the handler boundary: deny is absent, first is one request, unrestricted is rate-limited, and
/// the model's text comes back in the MCP result shape.
/// </summary>
public sealed class McpSamplingTests
{
    private static readonly ModelDescriptor Descriptor = new("fake", "model", "Fake");

    [Fact]
    public void DenyBuildsNoHandler()
    {
        var connection = Connect(new McpClientOptions());
        Assert.Null(McpSampling.Create(new Resolver(), Descriptor, connection, new McpClientOptions()));
    }

    [Fact]
    public async Task FirstAllowsOneRequestAndRefusesTheSecond()
    {
        var options = new McpClientOptions { SamplingPolicy = McpSamplingPolicy.First };
        var connection = Connect(options);
        var handler = McpSampling.Create(new Resolver(), Descriptor, connection, options);
        Assert.NotNull(handler);

        var first = await handler("sampling/createMessage", Parameters(), CancellationToken.None);
        Assert.Equal("sampled", first!["content"]!["text"]!.GetValue<string>());

        var error = await Assert.ThrowsAsync<McpSamplingException>(
            () => handler("sampling/createMessage", Parameters(), CancellationToken.None));
        Assert.Contains("first request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrestrictedStillHonoursTheCallCeiling()
    {
        var options = new McpClientOptions
        {
            SamplingPolicy = McpSamplingPolicy.Unrestricted,
            MaxSamplingCallsPerMinute = 1,
        };
        var connection = Connect(options);
        var handler = McpSampling.Create(new Resolver(), Descriptor, connection, options);
        Assert.NotNull(handler);

        await handler("sampling/createMessage", Parameters(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<McpSamplingException>(
            () => handler("sampling/createMessage", Parameters(), CancellationToken.None));
        Assert.Contains("calls per minute", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTextSamplingContentIsAccepted()
    {
        var options = new McpClientOptions { SamplingPolicy = McpSamplingPolicy.First };
        var connection = Connect(options);
        var handler = McpSampling.Create(new Resolver(), Descriptor, connection, options);
        Assert.NotNull(handler);

        using var document = JsonDocument.Parse(
            """{"messages":[{"role":"user","content":{"type":"image","data":"x","mimeType":"image/png"}}]}""");

        await Assert.ThrowsAsync<McpSamplingException>(
            () => handler("sampling/createMessage", document.RootElement.Clone(), CancellationToken.None));
    }

    [Fact]
    public async Task UnrestrictedStillHonoursTheInputCeiling()
    {
        var options = new McpClientOptions
        {
            SamplingPolicy = McpSamplingPolicy.Unrestricted,
            MaxSamplingInputTokensPerMinute = 1_000,
        };
        var connection = Connect(options);
        var handler = McpSampling.Create(new Resolver(), Descriptor, connection, options);
        Assert.NotNull(handler);

        using var document = JsonDocument.Parse(
            $$"""{"messages":[{"role":"user","content":"{{new string('x', 4_004)}}"}]}""");

        var error = await Assert.ThrowsAsync<McpSamplingException>(
            () => handler("sampling/createMessage", document.RootElement.Clone(), CancellationToken.None));
        Assert.Contains("input tokens per minute", error.Message, StringComparison.Ordinal);
    }

    private static McpServerConnection Connect(McpClientOptions options) =>
        new("fake", "fake-server", Stream.Null, Stream.Null, options);

    private static JsonElement Parameters()
    {
        using var document = JsonDocument.Parse(
            """{"messages":[{"role":"user","content":{"type":"text","text":"hello"}}],"maxTokens":16}""");
        return document.RootElement.Clone();
    }

    private sealed class Resolver : IChatModelResolver
    {
        private readonly FakeModel _model = new();
        public IStreamingChatModel Resolve(ModelDescriptor model) => _model;
    }

    private sealed class FakeModel : IStreamingChatModel
    {
        public ModelDescriptor Model { get; } = McpSamplingTests.Descriptor;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TextDelta("sampled");
            yield return new StreamCompleted(new ChatResponse(
                ChatMessage.Assistant("sampled"),
                FinishReason.Stop,
                new TokenUsage(1, 1)));
        }
    }
}
