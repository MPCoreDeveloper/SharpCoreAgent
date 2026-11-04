using System.Runtime.CompilerServices;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Evaluation;

/// <summary>Plays one immutable scenario script through the provider protocol used by the real loop.</summary>
internal sealed class RecordedChatModel(IReadOnlyList<RecordedTurn> turns) : IStreamingChatModel, IChatModelResolver
{
    public static ModelDescriptor Descriptor { get; } = new(
        "evaluation",
        "recorded",
        "Recorded evaluation",
        ContextWindow: 128_000);

    private int _next;

    public ModelDescriptor Model => Descriptor;

    public int RequestCount => _next;

    public IStreamingChatModel Resolve(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return this;
    }

    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> StreamCoreAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var position = _next++;
        if (position >= turns.Count)
        {
            throw new InvalidOperationException(
                $"the recorded script ended after {turns.Count} requests, but the agent requested another one");
        }

        var turn = turns[position];
        var calls = turn.Calls.Select((call, index) => new ToolCall(
            $"call-{position + 1}-{index + 1}",
            call.Name,
            call.Arguments.GetRawText())).ToArray();
        var message = ChatMessage.Assistant(turn.Text, calls);
        var usage = EstimateUsage(request, turn);

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (turn.Text.Length > 0)
        {
            yield return new TextDelta(turn.Text);
        }

        yield return new StreamCompleted(new ChatResponse(
            message,
            calls.Length > 0 ? FinishReason.ToolCalls : FinishReason.Stop,
            usage));
    }

    private static TokenUsage EstimateUsage(ChatRequest request, RecordedTurn turn)
    {
        // Dynamic tool output contains wall-clock timings. Count stable conversation content and recorded output so
        // this synthetic metric is reproducible; it is deliberately not an estimate of a live provider's full prompt.
        var inputCharacters = request.Messages
            .Where(message => message.Role != ChatRole.Tool)
            .Sum(message => message.Content.Length + message.ToolCalls.Sum(call => call.ArgumentsJson.Length))
            + request.Tools.Sum(tool => tool.Name.Length + tool.Description.Length + tool.ParametersSchemaJson.Length);
        var outputCharacters = turn.Text.Length + turn.Calls.Sum(call => call.Name.Length + call.Arguments.GetRawText().Length);
        return new TokenUsage(Math.Max(1, (inputCharacters / 4) + 1), Math.Max(1, (outputCharacters / 4) + 1));
    }
}

/// <summary>Approves only inside the runner's newly created fixture; it is never registered in the product CLI.</summary>
internal sealed class EvaluationApprover : IToolApprover
{
    public ValueTask<ApprovalOutcome> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ApprovalOutcome.Approved);
}
