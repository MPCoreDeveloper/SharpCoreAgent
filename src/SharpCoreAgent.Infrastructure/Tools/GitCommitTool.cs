using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Creates a Git commit from the staged index (<c>git commit</c>), through the shared process host.</summary>
/// <remarks>
/// Destructive because it records history. The argv is fixed and the model supplies only a bounded message and
/// optional workspace-relative paths. Hooks and commit signing are disabled by <see cref="GitCommand"/>'s write mode,
/// and the author identity is the user's own configuration — never a model-supplied <c>-c user.*</c>.
/// <see cref="IToolPreview"/> shows the proposed subject and scope before the write is approved.
/// </remarks>
public sealed class GitCommitTool : ITool, IToolPreview
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "git_commit";

    /// <summary>Gets the longest commit message the tool accepts, in characters.</summary>
    public const int MaxMessageLength = 4_000;

    private const int MaxSubjectLength = 72;
    private const int MaxPaths = 200;

    private const string Schema =
        """{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":4000,"description":"The commit message. The first line is the subject; keep it under 72 characters."},"paths":{"type":"array","items":{"type":"string"},"description":"Optional workspace-relative paths to commit; omit to commit whatever git_stage staged."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"required":["message"],"additionalProperties":false}""";

    private readonly GitCommand _git;

    /// <summary>Creates the tool with an owned process registry.</summary>
    public GitCommitTool()
        : this(new ShellProcessRegistry())
    {
    }

    /// <summary>Creates the tool over a shared shell process host.</summary>
    /// <param name="processes">The registry that owns the Git write.</param>
    /// <param name="gitFileName">The Git executable, normally <c>git</c>.</param>
    public GitCommitTool(ShellProcessRegistry processes, string gitFileName = GitCommand.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitFileName);
        _git = new GitCommand(processes, gitFileName);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Create a Git commit from staged changes (git commit). Destructive and approval-gated. Hooks and commit signing are disabled; the author is your configured identity. Pass paths to commit them directly, or omit them to commit what git_stage already staged.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.Destructive;

    /// <inheritdoc />
    public ValueTask<string?> DescribeChangeAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
            var subject = Subject(arguments.GetOptionalString("message"));
            var scope = DescribeScope(arguments.GetOptionalArray("paths"));
            return ValueTask.FromResult<string?>(
                string.Create(CultureInfo.InvariantCulture, $"commit: {subject}\nscope: {scope}"));
        }
        catch (Exception ex) when (ex is SharpCoreAgent.Domain.Errors.ToolArgumentException or FormatException)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        string message;
        try
        {
            message = arguments.GetRequiredString("message");
        }
        catch (SharpCoreAgent.Domain.Errors.ToolArgumentException ex)
        {
            return ToolResult.Failure(toolCall.Id, Name, ex.Message);
        }

        if (message.Length > MaxMessageLength)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(CultureInfo.InvariantCulture, $"the commit message must be at most {MaxMessageLength} characters."));
        }

        var timeout = TimeSpan.FromSeconds(
            arguments.GetOptionalInt("timeout_seconds", GitCommand.DefaultTimeoutSeconds, minimum: 1, maximum: 120));

        var rawPaths = arguments.GetOptionalArray("paths");
        if (rawPaths.Count > MaxPaths)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(CultureInfo.InvariantCulture, $"pass at most {MaxPaths} paths at once."));
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

        var command = new List<string> { "commit", "--no-verify", "-m", message };
        if (pathspecs.Count > 0)
        {
            command.Add("--");
            command.AddRange(pathspecs);
        }

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
                string.Create(CultureInfo.InvariantCulture, $"git commit did not finish within {timeout.TotalSeconds:0} seconds."));
        }

        if (outcome.ExitCode != 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, GitStatusTool.Failure("commit", outcome));
        }

        var sha = await ShortHeadAsync(context.Workspace, timeout, cancellationToken).ConfigureAwait(false);
        var subject = Subject(message);
        var text = TextLimits.Bound(
            string.Create(CultureInfo.InvariantCulture, $"committed {sha}: {subject}"),
            TextLimits.MaxCommandOutputChars,
            out var truncated);
        return ToolResult.Success(
            toolCall.Id,
            Name,
            text,
            new ToolEffect(ToolEffectKind.CommandRun, string.Concat("git commit -m \"", subject, "\"")))
            with { IsTruncated = truncated };
    }

    private async Task<string> ShortHeadAsync(Workspace workspace, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var head = await _git.RunAsync(workspace, ["rev-parse", "--short", "HEAD"], timeout, cancellationToken)
                .ConfigureAwait(false);
            return head.ExitCode == 0 && head.Output.Length > 0 ? head.Output.Trim() : "HEAD";
        }
        catch (InvalidOperationException)
        {
            return "HEAD";
        }
    }

    private static string DescribeScope(IReadOnlyList<JsonElement> paths)
    {
        var named = new List<string>(paths.Count);
        foreach (var element in paths)
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value)
            {
                named.Add(value);
            }
        }

        return named.Count == 0 ? "everything git_stage staged" : string.Join(", ", named);
    }

    private static string Subject(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(no message)";
        }

        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= MaxSubjectLength ? trimmed : string.Concat(trimmed[..MaxSubjectLength], "…");
            }
        }

        return "(no message)";
    }
}

