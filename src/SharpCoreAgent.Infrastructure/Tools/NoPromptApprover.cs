using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The approver a sub-agent runs behind: it refuses everything and never prints a question.
/// </summary>
/// <remarks>
/// Unreachable by construction today — <see cref="TaskTool"/> gives its child
/// <see cref="ApprovalMode.AllowAll"/> or <see cref="ApprovalMode.ReadOnly"/>, never
/// <see cref="ApprovalMode.Ask"/> — and it exists so that stays true by more than a comment. A modal prompt raised
/// from inside a <c>task</c> call would arrive while the developer is watching the parent turn, with no way to tell
/// which of two nested turns is asking, and the answer would be a decision about a transcript they cannot see.
/// <para>
/// Not the container's <c>DenyAllApprover</c>, which answers the same thing: that type lives in the composition root
/// and this one is compiled beside the tool, and three lines of "refuse" are cheaper than a reference between the
/// two layers.
/// </para>
/// </remarks>
internal sealed class NoPromptApprover : IToolApprover
{
    /// <summary>Gets the shared instance.</summary>
    internal static NoPromptApprover Instance { get; } = new();

    private NoPromptApprover()
    {
    }

    /// <inheritdoc />
    public ValueTask<ApprovalOutcome> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ApprovalOutcome.Denied);
}
