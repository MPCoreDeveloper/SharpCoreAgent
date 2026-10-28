using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Asks the developer, on the terminal, whether a destructive tool may run.
/// </summary>
/// <remarks>
/// Four answers, because two force the same decision to be retyped for every call in a turn: <c>y</c> runs
/// this one, <c>t</c> runs every later call of the same tool in this session (which is how a developer stops
/// being asked about <c>run_shell_command</c> without also waving through every file write), <c>a</c> runs
/// everything in this session, and anything else refuses. The default is still no: an empty line, a closed
/// stdin or an unrecognised word refuses the call, which is what makes
/// <c>sharpcoreagent run "…" &lt; /dev/null</c> safe. The refusal is printed here rather than in the
/// observer -- the developer who just typed the answer gets the confirmation, and the refusal text reads the
/// same in the transcript the model receives.
/// </remarks>
internal sealed class ConsoleApprover : IToolApprover
{
    private readonly TextReader _input;
    private readonly TextWriter _error;
    private readonly Func<bool> _isInteractive;
    private readonly AnsiTheme _theme;

    /// <summary>
    /// Creates an approver.
    /// </summary>
    /// <param name="input">Where answers are read from, normally <see cref="Console.In"/>.</param>
    /// <param name="error">Where the prompt and the refusal are written, normally <see cref="Console.Error"/>.</param>
    /// <param name="isInteractive">Reports whether the terminal can be asked at all.</param>
    /// <param name="theme">The palette for the question, the diff and the refusal; null renders plain text.</param>
    public ConsoleApprover(TextReader input, TextWriter error, Func<bool> isInteractive, AnsiTheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(isInteractive);

        _input = input;
        _error = error;
        _isInteractive = isInteractive;
        _theme = theme ?? AnsiTheme.Plain;
    }

    /// <inheritdoc />
    public ValueTask<ApprovalOutcome> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isInteractive())
        {
            return ValueTask.FromResult(Refuse(
                request,
                "input is not interactive, so nothing can be approved; rerun with --approval allow"));
        }

        _error.Write(Render(request));
        _error.Flush();

        var answer = _input.ReadLine();
        cancellationToken.ThrowIfCancellationRequested();

        if (answer is not null && (IsYes(answer, "y") || IsYes(answer, "yes")))
        {
            return ValueTask.FromResult(ApprovalOutcome.Approved);
        }

        if (answer is not null && IsThisTool(answer))
        {
            // The receipt matters as much here as it does for "every tool": the next call of this tool prints no
            // prompt, and a developer who cannot see what they agreed to will read the silence as a bug.
            _error.WriteLine(
                $"{_theme.Success("✔")} {_theme.Dim($"{request.ToolName} runs without asking for the rest of this session (not saved)")}");
            return ValueTask.FromResult(ApprovalOutcome.ApprovedForTool);
        }

        if (answer is not null && IsEveryWord(answer))
        {
            // The loop records the session-wide decision; this line is the receipt, because the next
            // tool call will not print a prompt the developer could read the answer off.
            _error.WriteLine(
                $"{_theme.Success("✔")} {_theme.Dim("every destructive tool runs in this session without asking (not saved)")}");
            return ValueTask.FromResult(ApprovalOutcome.ApprovedForSession);
        }

        return ValueTask.FromResult(Refuse(request, "the developer declined"));
    }

    private ApprovalOutcome Refuse(ToolApprovalRequest request, string reason)
    {
        _error.WriteLine($"{_theme.Failure("⨯")} tool refused: {request.ToolName} ({reason})");
        return ApprovalOutcome.Denied;
    }

    /// <summary>
    /// Renders the change, when the tool offered one, followed by the question.
    /// </summary>
    /// <param name="request">What is about to run.</param>
    /// <returns>The text to write before reading the answer.</returns>
    /// <remarks>
    /// The diff comes before the question on purpose: the developer must be able to answer by reading
    /// the lines above the cursor, not by scrolling back into the transcript after the answer.
    /// </remarks>
    private string Render(ToolApprovalRequest request)
    {
        var builder = new System.Text.StringBuilder();
        if (request.Preview is { Length: > 0 } preview)
        {
            builder.Append(_theme.Dim("change:")).Append('\n');
            builder.Append(_theme.Diff(preview));
            if (!preview.EndsWith('\n'))
            {
                builder.Append('\n');
            }
        }

        builder.Append(_theme.Dim("y = once   t = this tool for the session   a = every tool   N = no")).Append('\n');
        builder.Append(_theme.Accent("allow")).Append(' ')
            .Append(_theme.Strong(request.ToolName)).Append("? ")
            .Append(request.Summary).Append(' ')
            .Append(_theme.Dim("[y/t/a/N]")).Append(' ');
        return builder.ToString();
    }

    private static bool IsYes(string answer, string word) =>
        string.Equals(answer.Trim(), word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recognises the answer that stops the question for one tool.
    /// </summary>
    /// <param name="answer">The line the developer typed.</param>
    /// <returns><see langword="true"/> for <c>t</c> or <c>tool</c>.</returns>
    /// <remarks>
    /// Read before <see cref="IsEveryWord"/>, and deliberately not spelled <c>allow</c>: that word is one character
    /// away from <c>all</c> and means the opposite of what a developer typing it intends. <c>t</c> is what the prompt
    /// offers, and the longer form exists so the answer can be typed out.
    /// </remarks>
    private static bool IsThisTool(string answer) => IsYes(answer, "t") || IsYes(answer, "tool");

    /// <summary>
    /// Recognises the answer that applies to the rest of the conversation.
    /// </summary>
    /// <param name="answer">The line the developer typed.</param>
    /// <returns><see langword="true"/> for <c>a</c>, <c>all</c> or <c>always</c>.</returns>
    /// <remarks>
    /// <c>always</c> is offered because it is the word a developer reaches for; <c>all</c> because it is the
    /// word the approval mode uses. Like <c>y</c>, neither is inferred from a longer word, so <c>aye</c> or
    /// <c>allow</c> is still a refusal rather than a decision nobody meant to make.
    /// </remarks>
    private static bool IsEveryWord(string answer) =>
        IsYes(answer, "a") || IsYes(answer, "all") || IsYes(answer, "always");
}
