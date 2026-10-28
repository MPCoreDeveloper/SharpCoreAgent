using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;

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
    private readonly bool _verbose;
    private readonly AnsiTheme _theme;
    private readonly Dictionary<string, long> _started = new(StringComparer.Ordinal);
    private readonly Func<(long Spent, long Ceiling)>? _budget;
    private long? _turnStarted;
    private bool _thinkingOpen;
    private bool _textOpen;
    private bool _sawText;
    private bool _toolOutputOpen;

    /// <summary>The gutter that marks a line as tool output rather than model text.</summary>
    private const string Gutter = "  │ ";

    /// <summary>
    /// Creates an observer.
    /// </summary>
    /// <param name="output">The answer stream, normally <see cref="Console.Out"/>.</param>
    /// <param name="error">The diagnostics stream, normally <see cref="Console.Error"/>.</param>
    /// <param name="json">Whether to emit JSON lines instead of prose.</param>
    /// <param name="quiet">Whether to suppress thinking deltas, tool lines and the summary.</param>
    /// <param name="theme">The palette for glyphs and names; null renders plain text. Ignored when <paramref name="json"/> is set.</param>
    /// <param name="verbose">Whether to name each turn as it starts, so a wait before the first token is not silence.</param>
    /// <param name="budget">
    /// Reports the session's spend and ceiling when the closing summary is written, so a developer sees how much of
    /// the budget a turn consumed instead of discovering it when the budget runs out.
    /// </param>
    public ConsoleObserver(
        TextWriter output,
        TextWriter error,
        bool json,
        bool quiet,
        AnsiTheme? theme = null,
        bool verbose = false,
        Func<(long Spent, long Ceiling)>? budget = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _output = output;
        _error = error;
        _json = json;
        _quiet = quiet;
        _verbose = verbose;
        _theme = json ? AnsiTheme.Plain : theme ?? AnsiTheme.Plain;
        _budget = budget;
    }

    /// <inheritdoc />
    public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        switch (agentEvent)
        {
            case TurnStarted started:
                _turnStarted = Stopwatch.GetTimestamp();
                if (_json)
                {
                    WriteJson(new { type = "turn_started", model = started.ModelId });
                }
                else if (_verbose && !_quiet)
                {
                    // A turn can spend a while before its first token — a cold index, a slow provider, a
                    // retry — and without this the console says nothing at all. One dim line is the
                    // difference between a session that is working and one that looks frozen.
                    _error.WriteLine(_theme.Dim(
                        string.Create(CultureInfo.InvariantCulture, $"· {started.ModelId} · turn started")));
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

            case ContextCompacted compacted:
                RenderCompacted(compacted);
                break;

            case ToolOutputDelta output:
                RenderToolOutput(output);
                break;

            case ToolFinished finished:
                RenderFinished(finished);
                break;

            case FallbackAttempted fallback:
                RenderFallback(fallback.Fallback);
                break;

            case TurnFinished done:
                RenderFinished(done.Report);
                break;
        }

        _output.Flush();
        _error.Flush();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Says that the beginning of the conversation was dropped so the next request fits.
    /// </summary>
    /// <param name="compacted">How many messages went, and how many were kept.</param>
    /// <remarks>
    /// A warning rather than a dim note, because this is the moment a long session stops being able to
    /// answer questions about its own beginning: the developer needs to know that "as we discussed" now
    /// refers to a shorter discussion than it did a minute ago.
    /// </remarks>
    private void RenderCompacted(ContextCompacted compacted)
    {
        CloseThinkingLine();
        CloseTextLine();
        CloseToolOutputLine();

        if (_json)
        {
            WriteJson(new { type = "context_compacted", dropped = compacted.Dropped, kept = compacted.Kept });
            return;
        }

        if (_quiet)
        {
            return;
        }

        _error.WriteLine(_theme.Warning(string.Create(
            CultureInfo.InvariantCulture,
            $"! context: dropped {compacted.Dropped} old message(s), kept the system prompts and the last {compacted.Kept}")));
    }

    /// <summary>
    /// Renders the move to the fallback model, before that model's text arrives.
    /// </summary>
    /// <param name="fallback">Which attempt failed, why, and which model now serves the turn.</param>
    /// <remarks>
    /// The line goes to stderr with the other diagnostics, because an answer that came from a
    /// different provider than the prompt named is not part of the answer — but it is something the
    /// developer should read. The reason is the provider's own message, already stripped of any
    /// credential by the adapter.
    /// </remarks>
    private void RenderFallback(FallbackUsed fallback)
    {
        CloseThinkingLine();
        CloseTextLine();
        CloseToolOutputLine();

        if (_json)
        {
            WriteJson(new
            {
                type = "fallback",
                from = fallback.PrimaryModelId,
                to = fallback.FallbackModelId,
                status = fallback.StatusCode,
                reason = fallback.Reason,
            });
            return;
        }

        if (_quiet)
        {
            return;
        }

        _error.WriteLine($"{_theme.Failure("fallback:")} {fallback.ToString()} {_theme.Dim(fallback.Reason)}");
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
            _error.Write(_theme.Dim("· "));
            _thinkingOpen = true;
        }

        _error.Write(_theme.Dim(text));
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
        CloseToolOutputLine();

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

        _output.WriteLine(string.Concat(
            _theme.Accent("⏺ "),
            _theme.Strong(requested.ToolName),
            _theme.Dim(" " + Compact(requested.ArgumentsJson))));
    }

    /// <summary>
    /// Renders one increment of a running tool's output, indented under its tool line.
    /// </summary>
    /// <param name="output">The increment to show.</param>
    /// <remarks>
    /// Tool output goes to stderr for the same reason thinking does: it is a diagnostic about the work, not
    /// the answer, so redirecting stdout still captures exactly the model's final message. A chunk can end
    /// mid-line, so the gutter is written lazily and the open line is closed by whichever event ends the
    /// tool.
    /// </remarks>
    private void RenderToolOutput(ToolOutputDelta output)
    {
        if (string.IsNullOrEmpty(output.Text))
        {
            return;
        }

        if (_json)
        {
            WriteJson(new { type = "tool_output", callId = output.CallId, tool = output.ToolName, text = output.Text });
            return;
        }

        if (_quiet)
        {
            return;
        }

        CloseThinkingLine();
        CloseTextLine();

        var text = output.Text;
        var index = 0;
        while (index < text.Length)
        {
            var remaining = text.AsSpan(index);
            var newline = remaining.IndexOf('\n');
            var segment = newline < 0 ? remaining : remaining[..newline];

            if (!_toolOutputOpen)
            {
                _error.Write(_theme.Dim(Gutter));
                _toolOutputOpen = true;
            }

            if (segment.Length > 0)
            {
                _error.Write(_theme.Dim(segment.TrimEnd('\r').ToString()));
            }

            if (newline < 0)
            {
                break;
            }

            _error.WriteLine();
            _toolOutputOpen = false;
            index += newline + 1;
        }
    }

    private void CloseToolOutputLine()
    {
        if (!_toolOutputOpen)
        {
            return;
        }

        _error.WriteLine();
        _toolOutputOpen = false;
    }

    private void RenderFinished(ToolFinished finished)
    {
        CloseToolOutputLine();

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

        // An error is the one summary worth repeating: the model reads the tool's text, but the developer watching
        // the console needs the reason on the line that says something went wrong, not three lines above it.
        var reason = finished.IsError && !string.IsNullOrWhiteSpace(finished.Summary)
            ? _theme.Dim(" · " + Compact(finished.Summary))
            : string.Empty;

        _output.WriteLine(string.Concat(
            _theme.Dim("  ⎿ "),
            finished.IsError ? _theme.Failure("error") : _theme.Success("ok"),
            _theme.Dim(string.Create(CultureInfo.InvariantCulture, $" · {Duration(milliseconds)}")),
            reason));
    }

    private void RenderFinished(AgentReport report)
    {
        CloseThinkingLine();
        CloseTextLine();
        CloseToolOutputLine();

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
                fallbacks = report.Fallbacks.Select(fallback => new
                {
                    from = fallback.PrimaryModelId,
                    to = fallback.FallbackModelId,
                    status = fallback.StatusCode,
                    reason = fallback.Reason,
                }),
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

        var spend = string.Empty;
        if (_budget?.Invoke() is { Ceiling: > 0 } budget)
        {
            spend = Spend(budget.Spent, budget.Ceiling);
        }

        // An endpoint that reported no usage leaves nothing to add: "unknown in / unknown out" beside a real spend
        // reads as a broken number rather than a missing one.
        var usage = report.Usage.IsUnknown
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" · {report.Usage}");

        // A turn that took a minute and a turn that took a second look identical without this, and the developer
        // deciding whether to send the same request again is exactly the person who needs to know which it was.
        var elapsed = _turnStarted is { } started
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" · {Duration(Stopwatch.GetElapsedTime(started).TotalMilliseconds)}")
            : string.Empty;
        _turnStarted = null;

        _error.WriteLine(string.Concat(
            _theme.Accent("✻ "),
            Word(report.Outcome),
            _theme.Dim(string.Create(
                CultureInfo.InvariantCulture,
                $" · {report.Iterations} iteration(s){elapsed}{usage}{spend}"))));

        if (!string.IsNullOrWhiteSpace(report.Detail))
        {
            _error.WriteLine($"  {report.Detail}");
        }
    }

    /// <summary>
    /// Renders what a turn cost against its ceiling, for the closing summary.
    /// </summary>
    /// <param name="spent">The session's tokens so far.</param>
    /// <param name="ceiling">The session's ceiling; <see cref="long.MaxValue"/> means unlimited.</param>
    /// <returns>The suffix the summary line appends, such as <c> · 614/1,000 tokens</c>.</returns>
    /// <remarks>
    /// The unlimited sentinel is a number rather than infinity, so printing it raw would bury the spend under
    /// nineteen digits; it is named instead, the same word <c>/budget</c> uses.
    /// </remarks>
    private static string Spend(long spent, long ceiling) =>
        ceiling == long.MaxValue
            ? string.Create(CultureInfo.InvariantCulture, $" · {spent:n0} tokens (unlimited)")
            : string.Create(CultureInfo.InvariantCulture, $" · {spent:n0}/{ceiling:n0} tokens");

    /// <summary>
    /// Formats a duration the way a person reads one: milliseconds under a second, seconds beyond it.
    /// </summary>
    /// <param name="milliseconds">The measured duration.</param>
    /// <returns>Text such as <c>12 ms</c> or <c>1.4 s</c>.</returns>
    private static string Duration(double milliseconds) =>
        milliseconds < 1_000
            ? string.Create(CultureInfo.InvariantCulture, $"{milliseconds:0} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1_000:0.0} s");

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
