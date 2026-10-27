using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Asks the developer, on the terminal, whether a destructive tool may run.
/// </summary>
/// <remarks>
/// The default answer is no: an empty line, a closed stdin or any word other than <c>y</c>/<c>yes</c>
/// refuses the call. That default is what makes <c>sharpcoreagent run "…" &lt; /dev/null</c> safe, and it is
/// why the refusal is printed here rather than in the observer -- the developer who just typed the answer
/// gets the confirmation, and the refusal text reads the same in the transcript the model receives.
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

        builder.Append(_theme.Accent("allow")).Append(' ')
            .Append(_theme.Strong(request.ToolName)).Append("? ")
            .Append(request.Summary).Append(' ')
            .Append(_theme.Dim("[y/N]")).Append(' ');
        return builder.ToString();
    }

    private static bool IsYes(string answer, string word) =>
        string.Equals(answer.Trim(), word, StringComparison.OrdinalIgnoreCase);
}
