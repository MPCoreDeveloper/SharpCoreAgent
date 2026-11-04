using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Shows a scoped unified diff of the worktree, the index or everything changed since HEAD.
/// </summary>
/// <remarks>
/// Revision expressions are not arguments: the scope is a closed enum and the only path is confined to the workspace.
/// External diff drivers, textconv and colour are disabled, so the answer is Git's own text and no repository-configured
/// program is started to decorate it.
/// </remarks>
public sealed class GitDiffTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "git_diff";

    /// <summary>Gets the scope of unstaged changes.</summary>
    public const string Working = "working";

    /// <summary>Gets the scope of the index against HEAD.</summary>
    public const string Staged = "staged";

    /// <summary>Gets the scope of every tracked change against HEAD.</summary>
    public const string Head = "head";

    private const string Schema =
        """{"type":"object","properties":{"scope":{"type":"string","enum":["working","staged","head"],"description":"working = unstaged changes; staged = index against HEAD; head = all tracked changes against HEAD. Defaults to working."},"path":{"type":"string","description":"Optional workspace-relative file or directory to diff."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"additionalProperties":false}""";

    private readonly GitCommand _git;

    /// <summary>Creates the tool with an owned process registry.</summary>
    public GitDiffTool()
        : this(new ShellProcessRegistry())
    {
    }

    /// <summary>Creates the tool over a shared shell process host.</summary>
    /// <param name="processes">The registry that owns the Git observation.</param>
    /// <param name="gitFileName">The Git executable, normally <c>git</c>.</param>
    public GitDiffTool(ShellProcessRegistry processes, string gitFileName = GitCommand.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitFileName);
        _git = new GitCommand(processes, gitFileName);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Show a unified Git diff for the workspace: unstaged changes, staged changes, or everything tracked since HEAD. Read-only and bounded. Untracked files are not in a Git diff; call git_status to see them.",
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
        var scope = (arguments.GetOptionalString("scope") ?? Working).Trim().ToLowerInvariant();
        if (scope is not (Working or Staged or Head))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"scope must be '{Working}', '{Staged}' or '{Head}', not '{scope}'.");
        }

        var path = arguments.GetOptionalString("path");
        var timeout = TimeSpan.FromSeconds(
            arguments.GetOptionalInt("timeout_seconds", GitCommand.DefaultTimeoutSeconds, minimum: 1, maximum: 120));
        string? pathspec = null;
        if (path is { Length: > 0 })
        {
            if (!PathSafety.TryResolve(context.Workspace, path, out var fullPath, out var error))
            {
                return ToolResult.Refused(toolCall.Id, Name, error!);
            }

            pathspec = GitCommand.ToPathspec(context.Workspace, fullPath);
        }

        var command = new List<string>
        {
            "diff",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--unified=3",
        };
        if (scope == Staged)
        {
            command.Add("--cached");
        }
        else if (scope == Head)
        {
            command.Add("HEAD");
        }

        command.Add("--");
        command.Add(pathspec ?? ".");

        GitOutcome outcome;
        try
        {
            outcome = await _git.RunAsync(context.Workspace, command, timeout, cancellationToken).ConfigureAwait(false);
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
                string.Create(CultureInfo.InvariantCulture, $"git diff did not finish within {timeout.TotalSeconds:0} seconds."));
        }

        if (outcome.ExitCode != 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, GitStatusTool.Failure("diff", outcome));
        }

        var target = pathspec is null ? "." : pathspec;
        var heading = string.Create(CultureInfo.InvariantCulture, $"git diff {scope} {target}");
        var text = outcome.Output.Length == 0
            ? string.Concat(heading, "\nno changes")
            : string.Concat(heading, "\n", outcome.Output);
        var bounded = TextLimits.Bound(text, TextLimits.MaxCommandOutputChars, out var truncated);
        return ToolResult.Success(toolCall.Id, Name, bounded) with { IsTruncated = truncated };
    }
}
