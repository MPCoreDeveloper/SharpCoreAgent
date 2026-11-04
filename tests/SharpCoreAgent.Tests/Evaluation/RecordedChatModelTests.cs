using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Evaluation;
using Xunit;

namespace SharpCoreAgent.Tests.Evaluation;

public sealed class RecordedChatModelTests
{
    [Fact]
    public async Task DynamicToolTimingDoesNotChangeTheRecordedUsageMetric()
    {
        var turn = new RecordedTurn { Text = "done" };
        var first = await CompleteAsync(new RecordedChatModel([turn]), "duration 1.2 ms");
        var second = await CompleteAsync(new RecordedChatModel([turn]), "duration 12345.6789 ms");

        Assert.Equal(first.Usage.InputTokens, second.Usage.InputTokens);
        Assert.Equal(first.Usage.OutputTokens, second.Usage.OutputTokens);
        Assert.Equal(FinishReason.Stop, first.FinishReason);
        Assert.Equal("done", first.Text);
    }

    [Fact]
    public async Task CallIdentifiersAreStableAndOneBased()
    {
        var arguments = System.Text.Json.JsonDocument.Parse("""{"path":"value.txt"}""").RootElement.Clone();
        var model = new RecordedChatModel(
        [
            new RecordedTurn
            {
                Calls = [new RecordedCall { Name = "read_file", Arguments = arguments }],
            },
        ]);

        var response = await CompleteAsync(model, string.Empty);

        var call = Assert.Single(response.Message.ToolCalls);
        Assert.Equal("call-1-1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"value.txt\"}", call.ArgumentsJson);
    }

    private static async Task<ChatResponse> CompleteAsync(RecordedChatModel model, string toolOutput)
    {
        var request = new ChatRequest(
            RecordedChatModel.Descriptor,
            [
                ChatMessage.System("system"),
                new ChatMessage
                {
                    Role = ChatRole.Tool,
                    Content = toolOutput,
                    ToolCallId = "call-1",
                    ToolName = "run_shell",
                },
            ],
            []);
        await foreach (var streamEvent in model.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            if (streamEvent is StreamCompleted completed)
            {
                return completed.Response;
            }
        }

        throw new InvalidOperationException("the recorded stream did not complete");
    }
}
