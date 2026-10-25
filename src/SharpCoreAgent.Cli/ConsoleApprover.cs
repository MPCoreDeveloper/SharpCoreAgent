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

    /// <summary>
    /// Creates an approver.
    /// </summary>
    /// <param name="input">Where answers are read from, normally <see cref="Console.In"/>.</param>
    /// <param name="error">Where the prompt and the refusal are written, normally <see cref="Console.Error"/>.</param>
    /// <param name="isInteractive">Reports whether the terminal can be asked at all.</param>
    public ConsoleApprover(TextReader input, TextWriter error, Func<bool> isInteractive)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(isInteractive);

        _input = input;
        _error = error;
        _isInteractive = isInteractive;
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

        _error.Write($"allow {request.ToolName}? {request.Summary} [y/N] ");
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
        _error.WriteLine($"⨯ tool refused: {request.ToolName} ({reason})");
        return ApprovalOutcome.Denied;
    }

    private static bool IsYes(string answer, string word) =>
        string.Equals(answer.Trim(), word, StringComparison.OrdinalIgnoreCase);
}
