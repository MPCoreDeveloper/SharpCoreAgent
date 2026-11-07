using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Lists branches (<c>git branch --list</c>), read-only, through the shared process host.</summary>
/// <remarks>
/// Read-only, so Plan and Review can see the branches the way they see <c>git_status</c> and <c>git_diff</c> — the whole
/// reason the Git evidence tools are <see cref="ToolSafety.ReadOnly"/>. The command is fixed and takes no model input
/// beyond an optional timeout. Creating and deleting a branch is the separate, destructive
/// <see cref="GitBranchWriteTool"/>.
/// </remarks>
public sealed class GitBranchTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "git_branch";

    private const string Schema =
        """{"type":"object","properties":{"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"additionalProperties":false}""";

    private readonly GitCommand _git;

    /// <summary>Creates the tool with an owned process registry.</summary>
    public GitBranchTool()
        : this(new ShellProcessRegistry())
    {
    }

    /// <summary>Creates the tool over a shared shell process host.</summary>
    /// <param name="processes">The registry that owns the Git read.</param>
    /// <param name="gitFileName">The Git executable, normally <c>git</c>.</param>
    public GitBranchTool(ShellProcessRegistry processes, string gitFileName = GitCommand.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitFileName);
        _git = new GitCommand(processes, gitFileName);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "List Git branches (git branch). Read-only, so it is available in Plan and Review like git_status and git_diff. The current branch is marked with '*'. Use git_branch_write to create or delete a branch.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.ReadOnly;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var timeout = TimeSpan.FromSeconds(
            arguments.GetOptionalInt("timeout_seconds", GitCommand.DefaultTimeoutSeconds, minimum: 1, maximum: 120));

        GitOutcome outcome;
        try
        {
            outcome = await _git.RunAsync(context.Workspace, ["branch", "--list", "--no-color"], timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Failure(toolCall.Id, Name, ex.Message);
        }

        if (outcome.TimedOut)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(CultureInfo.InvariantCulture, $"git branch did not finish within {timeout.TotalSeconds:0} seconds."));
        }

        if (outcome.ExitCode != 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, GitStatusTool.Failure("branch", outcome));
        }

        var text = outcome.Output.Length == 0 ? "no branches" : outcome.Output;
        var bounded = TextLimits.Bound(text, TextLimits.MaxCommandOutputChars, out var truncated);
        return ToolResult.Success(toolCall.Id, Name, bounded) with { IsTruncated = truncated };
    }
}
