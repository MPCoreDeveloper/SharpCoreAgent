using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Renders the agent's events for a developer: streamed text, tool lines and a closing summary.
/// </summary>
/// <remarks>
/// Answer text goes to stdout because that is what a pipe should capture; thinking, the summary and every
/// diagnostic go to stderr, so <c>sharpcoreagent run "…" &gt; answer.txt</c> writes the answer and nothing else.
/// With <c>--json</c> the observer emits one object per line on stdout instead and never writes prose, which
/// is what makes the machine mode trustworthy: the same events, no formatting decisions.
/// </remarks>
internal sealed class ConsoleObserver : IAgentObserver
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _json;
    private readonly bool _quiet;
    private readonly Dictionary<string, long> _started = new(StringComparer.Ordinal);
    private bool _thinkingOpen;
    private bool _textOpen;
    private bool _sawText;

    /// <summary>
    /// Creates an observer.
    /// </summary>
    /// <param name="output">The answer stream, normally <see cref="Console.Out"/>.</param>
    /// <param name="error">The diagnostics stream, normally <see cref="Console.Error"/>.</param>
    /// <param name="json">Whether to emit JSON lines instead of prose.</param>
    /// <param name="quiet">Whether to suppress thinking deltas, tool lines and the summary.</param>
    public ConsoleObserver(TextWriter output, TextWriter error, bool json, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _output = output;
        _error = error;
        _json = json;
        _quiet = quiet;
    }

    /// <inheritdoc />
    public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        switch (agentEvent)
        {
            case TurnStarted started:
                if (_json)
                {
                    WriteJson(new { type = "turn_started", model = started.ModelId });
                }

                break;

            case AgentThinkingDelta thinking:
                RenderThinking(thinking.Text);
                break;

            case AgentTextDelta text:
                RenderText(text.Text);
                break;

            case ToolRequested requested:
                RenderRequested(requested);
                break;

            case ToolFinished finished:
                RenderFinished(finished);
                break;

            case TurnFinished done:
                RenderFinished(done.Report);
                break;
        }

        _output.Flush();
        _error.Flush();
        return ValueTask.CompletedTask;
    }

    private void RenderThinking(string text)
    {
        if (_json)
        {
            WriteJson(new { type = "thinking_delta", text });
            return;
        }

        if (_quiet)
        {
            return;
        }

        CloseTextLine();
        if (!_thinkingOpen)
        {
            _error.Write("· ");
            _thinkingOpen = true;
        }

        _error.Write(text);
    }

    private void RenderText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_json)
        {
            WriteJson(new { type = "text_delta", text });
            return;
        }

        CloseThinkingLine();
        _sawText = true;
        _output.Write(text);
        _textOpen = !text.EndsWith('\n');
    }

    private void RenderRequested(ToolRequested requested)
    {
        _started[requested.CallId] = Stopwatch.GetTimestamp();
        CloseThinkingLine();
        CloseTextLine();

        if (_json)
        {
            WriteJson(new
            {
                type = "tool_started",
                callId = requested.CallId,
                tool = requested.ToolName,
                arguments = Arguments(requested.ArgumentsJson),
            });
            return;
        }

        if (_quiet)
        {
            return;
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"→ {requested.ToolName}({Compact(requested.ArgumentsJson)})"));
    }

    private void RenderFinished(ToolFinished finished)
    {
        var now = Stopwatch.GetTimestamp();
        var milliseconds = _started.Remove(finished.CallId, out var started)
            ? Stopwatch.GetElapsedTime(started, now).TotalMilliseconds
            : 0;

        if (_json)
        {
            WriteJson(new
            {
                type = "tool_finished",
                callId = finished.CallId,
                tool = finished.ToolName,
                ok = !finished.IsError,
                summary = finished.Summary,
                ms = (long)milliseconds,
            });
            return;
        }

        if (_quiet)
        {
            return;
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"← {finished.ToolName}: {(finished.IsError ? "error" : "ok")} ({milliseconds:0} ms)"));
    }

    private void RenderFinished(AgentReport report)
    {
        CloseThinkingLine();
        CloseTextLine();

        if (_json)
        {
            WriteJson(new
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
            });
            return;
        }

        if (!_sawText && !string.IsNullOrWhiteSpace(report.FinalText))
        {
            _output.WriteLine(report.FinalText);
        }

        if (_quiet)
        {
            return;
        }

        _error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"— {Word(report.Outcome)} · {report.Iterations} iteration(s) · {report.Usage}"));

        if (!string.IsNullOrWhiteSpace(report.Detail))
        {
            _error.WriteLine($"  {report.Detail}");
        }
    }

    private void CloseThinkingLine()
    {
        if (!_thinkingOpen)
        {
            return;
        }

        _error.WriteLine();
        _thinkingOpen = false;
    }

    private void CloseTextLine()
    {
        if (!_textOpen)
        {
            return;
        }

        _output.WriteLine();
        _textOpen = false;
    }

    private void WriteJson(object payload) => _output.WriteLine(JsonSerializer.Serialize(payload));

    private static string Word(AgentOutcome outcome) => outcome switch
    {
        AgentOutcome.Completed => "completed",
        AgentOutcome.BudgetExhausted => "budget exhausted",
        AgentOutcome.Cancelled => "cancelled",
        _ => "failed",
    };

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

    private static string Compact(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        var flat = string.Join(' ', json.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }
}
