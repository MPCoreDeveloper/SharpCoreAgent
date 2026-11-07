using System.Text.Json;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Turns the agent's events into the JSON lines the page reads.
/// </summary>
/// <remarks>
/// The shapes are the ones <see cref="ConsoleObserver"/> already emits under <c>--json</c> — the same keys, so a
/// transcript captured from a terminal and one streamed to a page agree line for line. The stream is a closed set:
/// text, reasoning, the four tool shapes, a compaction notice, a fallback notice, the closing report, and the two
/// approval frames the browser answers.
/// </remarks>
internal static class ServeEvents
{
    /// <summary>Builds the frame for a turn that began.</summary>
    /// <param name="modelId">The <c>provider:model</c> name.</param>
    /// <returns>One JSON line.</returns>
    internal static string TurnStarted(string modelId) =>
        JsonSerializer.Serialize(new { type = "turn_started", model = modelId });

    /// <summary>Builds the frame for reasoning text.</summary>
    /// <param name="text">The increment.</param>
    /// <returns>One JSON line.</returns>
    internal static string Thinking(string text) =>
        JsonSerializer.Serialize(new { type = "thinking_delta", text });

    /// <summary>Builds the frame for answer text.</summary>
    /// <param name="text">The increment.</param>
    /// <returns>One JSON line.</returns>
    internal static string Text(string text) =>
        JsonSerializer.Serialize(new { type = "text_delta", text });

    /// <summary>Builds the frame for a tool the model asked for.</summary>
    /// <param name="callId">The call identifier.</param>
    /// <param name="tool">The tool name.</param>
    /// <param name="argumentsJson">The raw arguments; embedded as JSON when they parse, as text otherwise.</param>
    /// <returns>One JSON line.</returns>
    internal static string ToolStarted(string callId, string tool, string argumentsJson) =>
        JsonSerializer.Serialize(new
        {
            type = "tool_started",
            callId,
            tool,
            arguments = Arguments(argumentsJson),
        });

    /// <summary>Builds the frame for a running tool's output.</summary>
    /// <param name="callId">The call identifier.</param>
    /// <param name="tool">The tool name.</param>
    /// <param name="text">The increment; it may end mid-line.</param>
    /// <returns>One JSON line.</returns>
    internal static string ToolOutput(string callId, string tool, string text) =>
        JsonSerializer.Serialize(new { type = "tool_output", callId, tool, text });

    /// <summary>Builds the frame for a tool that finished.</summary>
    /// <param name="callId">The call identifier.</param>
    /// <param name="tool">The tool name.</param>
    /// <param name="ok">Whether the result is a success rather than a failure or refusal.</param>
    /// <param name="summary">One line describing the outcome.</param>
    /// <returns>One JSON line.</returns>
    internal static string ToolFinished(string callId, string tool, bool ok, string summary) =>
        JsonSerializer.Serialize(new { type = "tool_finished", callId, tool, ok, summary });

    /// <summary>Builds the frame for a transcript compaction.</summary>
    /// <param name="dropped">How many messages were replaced by the elision note.</param>
    /// <param name="kept">How many of the most recent messages were kept.</param>
    /// <returns>One JSON line.</returns>
    internal static string ContextCompacted(int dropped, int kept) =>
        JsonSerializer.Serialize(new { type = "context_compacted", dropped, kept });

    /// <summary>Builds the frame for a move to the fallback model.</summary>
    /// <param name="fallback">Which attempt failed, why, and which model answered.</param>
    /// <returns>One JSON line.</returns>
    internal static string Fallback(FallbackUsed fallback) =>
        JsonSerializer.Serialize(new
        {
            type = "fallback",
            from = fallback.PrimaryModelId,
            to = fallback.FallbackModelId,
            status = fallback.StatusCode,
            reason = fallback.Reason,
        });

    /// <summary>Builds the frame for the turn's closing report.</summary>
    /// <param name="report">The report also returned to the caller.</param>
    /// <returns>One JSON line.</returns>
    internal static string Report(AgentReport report) =>
        JsonSerializer.Serialize(new
        {
            type = "report",
            outcome = Word(report.Outcome),
            model = report.ModelId,
            iterations = report.Iterations,
            inputTokens = report.Usage.InputTokens,
            outputTokens = report.Usage.OutputTokens,
            files = report.FilesTouched,
            commands = report.CommandsRun,
            detail = report.Detail,
            unverified = report.IsUnverified,
        });

    /// <summary>Builds the frame that asks the page to approve a destructive tool.</summary>
    /// <param name="id">The approval identifier the page answers with.</param>
    /// <param name="tool">The tool name.</param>
    /// <param name="summary">One line naming the target.</param>
    /// <param name="argumentsJson">The raw arguments.</param>
    /// <param name="preview">The change, when the tool offered one.</param>
    /// <returns>One JSON line.</returns>
    internal static string ApprovalRequested(long id, string tool, string summary, string argumentsJson, string? preview) =>
        JsonSerializer.Serialize(new
        {
            type = "approval_requested",
            id,
            tool,
            summary,
            arguments = Arguments(argumentsJson),
            preview,
        });

    /// <summary>Builds the frame that reports how an approval was answered.</summary>
    /// <param name="id">The approval identifier.</param>
    /// <param name="decision">The decision, as a word the page shows.</param>
    /// <returns>One JSON line.</returns>
    internal static string ApprovalResolved(long id, string decision) =>
        JsonSerializer.Serialize(new { type = "approval_resolved", id, decision });

    /// <summary>Builds a one-off note frame.</summary>
    /// <param name="message">The sentence to show.</param>
    /// <returns>One JSON line.</returns>
    internal static string Notice(string message) =>
        JsonSerializer.Serialize(new { type = "notice", message });

    /// <summary>Builds the frame for a turn that failed outside the loop's own reporting.</summary>
    /// <param name="message">The reason.</param>
    /// <returns>One JSON line.</returns>
    internal static string Error(string message) =>
        JsonSerializer.Serialize(new { type = "error", message });

    private static string Word(AgentOutcome outcome) => outcome switch
    {
        AgentOutcome.Completed => "completed",
        AgentOutcome.BudgetExhausted => "budget exhausted",
        AgentOutcome.Cancelled => "cancelled",
        _ => "failed",
    };

    /// <summary>
    /// Embeds a tool's arguments as JSON when they parse, and as text when they do not.
    /// </summary>
    /// <param name="json">The raw argument JSON.</param>
    /// <returns>A <see cref="JsonElement"/> to embed, or the original string.</returns>
    /// <remarks>
    /// The same fallback <see cref="ConsoleObserver"/> uses: a model that emits malformed arguments must still be
    /// readable, and the page treats a string and an object identically because it prints whatever arrives.
    /// </remarks>
    private static object Arguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
