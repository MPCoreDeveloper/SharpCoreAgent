using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Stages workspace paths for the next commit (<c>git add</c>), through the shared process host.</summary>
/// <remarks>
/// Destructive because it writes the index. The argv is fixed; the model supplies only workspace-relative paths,
/// each confined by <see cref="PathSafety"/> before Git starts and placed after <c>--</c>. The write path drops
/// <c>--no-optional-locks</c> and disables repository hooks and signing (see <see cref="GitCommand"/>).
/// </remarks>
public sealed class GitStageTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "git_stage";

    /// <summary>Gets how many paths one call may stage.</summary>
    public const int MaxPaths = 200;

    private const string Schema =
        """{"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Workspace-relative files or directories to stage."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"required":["paths"],"additionalProperties":false}""";

    private readonly GitCommand _git;

    /// <summary>Creates the tool with an owned process registry.</summary>
    public GitStageTool()
        : this(new ShellProcessRegistry())
    {
    }

    /// <summary>Creates the tool over a shared shell process host.</summary>
    /// <param name="processes">The registry that owns the Git write.</param>
    /// <param name="gitFileName">The Git executable, normally <c>git</c>.</param>
    public GitStageTool(ShellProcessRegistry processes, string gitFileName = GitCommand.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitFileName);
        _git = new GitCommand(processes, gitFileName);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Stage workspace paths for the next commit (git add -A). Destructive and approval-gated. Paths stay inside the workspace. Use git_diff scope=staged afterwards to confirm what a git_commit would record.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.Destructive;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var rawPaths = arguments.GetOptionalArray("paths");
        var timeout = TimeSpan.FromSeconds(
            arguments.GetOptionalInt("timeout_seconds", GitCommand.DefaultTimeoutSeconds, minimum: 1, maximum: 120));

        if (rawPaths.Count == 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, "paths must name at least one workspace-relative file or directory.");
        }

        if (rawPaths.Count > MaxPaths)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(CultureInfo.InvariantCulture, $"stage at most {MaxPaths} paths at once."));
        }

        var pathspecs = new List<string>(rawPaths.Count);
        foreach (var element in rawPaths)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return ToolResult.Failure(toolCall.Id, Name, "every entry in paths must be a string.");
            }

            var path = element.GetString();
            if (string.IsNullOrWhiteSpace(path))
            {
                return ToolResult.Failure(toolCall.Id, Name, "paths must not contain an empty entry.");
            }

            if (!PathSafety.TryResolve(context.Workspace, path, out var fullPath, out var error))
            {
                return ToolResult.Refused(toolCall.Id, Name, error!);
            }

            pathspecs.Add(GitCommand.ToPathspec(context.Workspace, fullPath));
        }

        var command = new List<string> { "add", "-A", "--" };
        command.AddRange(pathspecs);

        GitOutcome outcome;
        try
        {
            outcome = await _git.RunAsync(context.Workspace, command, timeout, cancellationToken, write: true)
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
                string.Create(CultureInfo.InvariantCulture, $"git add did not finish within {timeout.TotalSeconds:0} seconds."));
        }

        if (outcome.ExitCode != 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, GitStatusTool.Failure("add", outcome));
        }

        var text = TextLimits.Bound(
            string.Create(
                CultureInfo.InvariantCulture,
                $"staged {pathspecs.Count} path(s): {string.Join(", ", pathspecs)}"),
            TextLimits.MaxCommandOutputChars,
            out var truncated);
        return ToolResult.Success(
            toolCall.Id,
            Name,
            text,
            new ToolEffect(ToolEffectKind.CommandRun, string.Concat("git add -A -- ", string.Join(' ', pathspecs))))
            with { IsTruncated = truncated };
    }
}
