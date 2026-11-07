using System.Collections.Concurrent;
using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Asks the page, over the stream, whether a destructive tool may run.
/// </summary>
/// <remarks>
/// The answer arrives on a different request than the one the question was raised under, which is the whole shape of a
/// browser conversation: the turn holds a <see cref="TaskCompletionSource{TResult}"/> open while <c>POST /api/approval</c>
/// completes it. Cancellation — a turn interrupted with Ctrl+C, or a page that closed — denies rather than hangs, so a
/// tool that would have written a file simply does not. The scope words match <see cref="ConsoleApprover"/>: once, this
/// tool for the session, every tool for the session, or no.
/// </remarks>
/// <param name="hub">Where the approval frames go.</param>
internal sealed class WebApprover(ServeHub hub) : IToolApprover
{
    private readonly ServeHub _hub = hub;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ApprovalOutcome>> _pending = new();
    private long _next;

    /// <inheritdoc />
    public async ValueTask<ApprovalOutcome> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = Interlocked.Increment(ref _next);
        var source = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = source;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hub.Publish(ServeEvents.ApprovalRequested(id, request.ToolName, request.Summary, request.ArgumentsJson, request.Preview));

            var decision = await source.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _hub.Publish(ServeEvents.ApprovalResolved(id, Name(decision)));
            return decision;
        }
        catch (OperationCanceledException)
        {
            // The turn was interrupted with the question open: deny, so nothing is written on a default.
            return ApprovalOutcome.Denied;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Completes a pending approval with the page's answer.
    /// </summary>
    /// <param name="id">The approval identifier the page echoed back.</param>
    /// <param name="decision">The word the page sent.</param>
    /// <returns><see langword="true"/> when a question was waiting for this answer.</returns>
    internal bool Resolve(long id, string decision) =>
        _pending.TryGetValue(id, out var source) && source.TrySetResult(Parse(decision));

    /// <summary>Maps the page's word to the loop's decision.</summary>
    /// <param name="decision">The word the page sent.</param>
    /// <returns>The outcome the loop acts on.</returns>
    /// <remarks>
    /// An unrecognised word denies, which is the same safe default <see cref="ConsoleApprover"/> takes for an empty line
    /// or a closed stdin: the only answer that costs a file is the one made by accident.
    /// </remarks>
    internal static ApprovalOutcome Parse(string? decision) => decision?.Trim().ToLowerInvariant() switch
    {
        "approve" or "yes" or "y" or "once" => ApprovalOutcome.Approved,
        "tool" or "t" => ApprovalOutcome.ApprovedForTool,
        "session" or "all" or "always" or "a" => ApprovalOutcome.ApprovedForSession,
        _ => ApprovalOutcome.Denied,
    };

    private static string Name(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.Approved => "approved",
        ApprovalOutcome.ApprovedForTool => "this tool",
        ApprovalOutcome.ApprovedForSession => "every tool",
        _ => "denied",
    };
}
