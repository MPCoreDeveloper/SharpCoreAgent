using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Domain.Agents;

/// <summary>
/// How much the developer wants to be asked before a tool runs.
/// </summary>
public enum ApprovalMode
{
    /// <summary>Only read-only tools may run; everything else is refused without asking.</summary>
    ReadOnly,

    /// <summary>Read-only tools run; destructive tools ask first.</summary>
    Ask,

    /// <summary>Every tool runs without asking. Only for trusted workspaces and unattended runs.</summary>
    AllowAll,
}

/// <summary>
/// The developer's answer to one approval prompt.
/// </summary>
public enum ApprovalOutcome
{
    /// <summary>The tool may run.</summary>
    Approved,

    /// <summary>The tool must not run; the refusal is reported back to the model.</summary>
    Denied,

    /// <summary>The tool may run, and so may every destructive tool for the rest of the session.</summary>
    /// <remarks>
    /// The third answer exists because the second question is usually the same as the first: a developer
    /// who approves <c>write_file</c> three times in one turn is not deciding anything after the first
    /// time, they are typing. The loop turns this outcome into <see cref="ApprovalMode.AllowAll"/> on the
    /// session -- in memory only, so the decision dies with the transcript and never becomes a preference
    /// the next process inherits by accident.
    /// </remarks>
    ApprovedForSession,

    /// <summary>The tool may run, and so may every later call of that same tool in this session.</summary>
    /// <remarks>
    /// Narrower than <see cref="ApprovedForSession"/> on purpose: a developer who is tired of approving
    /// <c>run_shell_command</c> while still wanting to read every file edit says this instead. The loop
    /// records the tool name on the session (<see cref="IToolApprover"/> implementations never do), which
    /// is what keeps the scope to one conversation and one tool: the next process, and the next tool,
    /// ask again.
    /// </remarks>
    ApprovedForTool,
}

/// <summary>
/// What the developer is shown before approving a destructive tool.
/// </summary>
/// <param name="ToolName">The tool.</param>
/// <param name="Safety">Its safety class, always <see cref="ToolSafety.Destructive"/> here.</param>
/// <param name="Summary">One line naming the target, so the prompt does not require reading JSON.</param>
/// <param name="ArgumentsJson">The raw arguments, for the developer who wants the detail.</param>
public sealed record ToolApprovalRequest(string ToolName, ToolSafety Safety, string Summary, string ArgumentsJson)
{
    /// <summary>
    /// Gets the change the call would make, rendered for a human, or <see langword="null"/> when the
    /// tool cannot describe one.
    /// </summary>
    /// <remarks>
    /// A file edit supplies a unified diff here, which is what turns the prompt from "trust this JSON"
    /// into "read this change". It is optional because a destructive tool without a meaningful preview
    /// -- deleting a directory, starting a process -- should not pretend to have one.
    /// </remarks>
    public string? Preview { get; init; }
}

/// <summary>
/// Decides whether a destructive tool may run.
/// </summary>
/// <remarks>
/// Approval lives behind a port so the loop has no opinion about terminals: the CLI plugs in a
/// console prompt, tests plug in a script, and an unattended run plugs in a policy that always denies.
/// </remarks>
public interface IToolApprover
{
    /// <summary>
    /// Asks for a decision.
    /// </summary>
    /// <param name="request">What is about to run.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <returns>The decision.</returns>
    ValueTask<ApprovalOutcome> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}
