using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Reports the working tree, index and untracked files without running a shell.</summary>
/// <remarks>
/// The command is fixed: <c>--porcelain=v1 -z</c>, all untracked files, a workspace-scoped pathspec and an optional
/// workspace-relative path. Nothing the model writes reaches Git as an option. Optional index locks and the fsmonitor
/// hook are disabled, so the observation does not refresh or rewrite the index it reports.
/// </remarks>
public sealed class GitStatusTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "git_status";

    /// <summary>Gets how many status entries one result renders before summarising the rest.</summary>
    public const int MaxEntries = 500;

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Optional workspace-relative file or directory to scope the status to."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"additionalProperties":false}""";

    private readonly GitCommand _git;

    /// <summary>Creates the tool with an owned process registry.</summary>
    public GitStatusTool()
        : this(new ShellProcessRegistry())
    {
    }

    /// <summary>Creates the tool over a shared shell process host.</summary>
    /// <param name="processes">The registry that owns the Git observation.</param>
    /// <param name="gitFileName">The Git executable, normally <c>git</c>.</param>
    public GitStatusTool(ShellProcessRegistry processes, string gitFileName = GitCommand.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitFileName);
        _git = new GitCommand(processes, gitFileName);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Show the Git working tree as porcelain status: staged and unstaged changes, untracked files, renames and conflicts. Read-only, bounded and scoped to the workspace. The first two columns are index and worktree status: M=modified, A=added, D=deleted, R=renamed, C=copied, U=unmerged, ?=untracked.",
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
            "status",
            "--porcelain=v1",
            "-z",
            "--untracked-files=all",
            "--ignore-submodules=all",
            "--",
            pathspec ?? ".",
        };

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
                string.Create(CultureInfo.InvariantCulture, $"git status did not finish within {timeout.TotalSeconds:0} seconds."));
        }

        if (outcome.ExitCode != 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, Failure("status", outcome));
        }

        var (entries, more) = Parse(outcome.Output);
        var heading = pathspec is null
            ? string.Create(CultureInfo.InvariantCulture, $"git status · {entries.Count} change(s)")
            : string.Create(CultureInfo.InvariantCulture, $"git status {pathspec} · {entries.Count} change(s)");
        var builder = new StringBuilder(heading);
        if (entries.Count == 0)
        {
            builder.Append("\nclean working tree");
        }
        else
        {
            foreach (var entry in entries)
            {
                builder.Append('\n').Append(entry);
            }
        }

        if (more > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n[truncated: {more} more change(s)]");
        }

        var text = TextLimits.Bound(builder.ToString(), TextLimits.MaxCommandOutputChars, out var truncated);
        return ToolResult.Success(toolCall.Id, Name, text) with { IsTruncated = truncated || more > 0 };
    }

    internal static (IReadOnlyList<string> Entries, int More) Parse(string output)
    {
        if (output.Length == 0)
        {
            return ([], 0);
        }

        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<string>();
        var more = 0;
        var index = 0;
        while (index < records.Length)
        {
            var record = records[index++];
            if (record.Length < 4)
            {
                continue;
            }

            var status = record[..2];
            var path = record[3..];
            if (status[0] is 'R' or 'C' && index < records.Length)
            {
                path = string.Concat(records[index++], " -> ", path);
            }

            if (entries.Count < MaxEntries)
            {
                entries.Add(string.Concat(status, " ", path));
            }
            else
            {
                more++;
            }
        }

        return (entries, more);
    }

    internal static string Failure(string operation, GitOutcome outcome)
    {
        var detail = TextLimits.Bound(outcome.Output, 2_000, out _);
        return detail.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"git {operation} failed with exit code {outcome.ExitCode}.")
            : string.Create(CultureInfo.InvariantCulture, $"git {operation} failed with exit code {outcome.ExitCode}: {detail}");
    }
}
