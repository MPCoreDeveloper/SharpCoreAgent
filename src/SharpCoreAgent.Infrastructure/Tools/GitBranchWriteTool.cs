using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Creates or deletes a branch (<c>git branch</c>), destructive and approval-gated.</summary>
/// <remarks>
/// The writable half of the branch surface; listing is the read-only <see cref="GitBranchTool"/>. A name is validated
/// against Git's ref rules and confirmed with <c>git check-ref-format</c> before it reaches <c>git branch</c>, and a
/// leading <c>-</c> is refused so a name can never become an option. Deleting the checked-out branch is refused.
/// </remarks>
public sealed class GitBranchWriteTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "git_branch_write";

    /// <summary>Gets the operation that creates a branch.</summary>
    public const string Create = "create";

    /// <summary>Gets the operation that deletes a branch.</summary>
    public const string Delete = "delete";

    private const string Schema =
        """{"type":"object","properties":{"op":{"type":"string","enum":["create","delete"],"description":"create makes a branch; delete removes one (not the checked-out branch)."},"name":{"type":"string","description":"Branch name; validated by git check-ref-format."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"required":["op","name"],"additionalProperties":false}""";

    private static readonly System.Buffers.SearchValues<char> Forbidden =
        System.Buffers.SearchValues.Create(" ~^:?*[\\");

    private readonly GitCommand _git;

    /// <summary>Creates the tool with an owned process registry.</summary>
    public GitBranchWriteTool()
        : this(new ShellProcessRegistry())
    {
    }

    /// <summary>Creates the tool over a shared shell process host.</summary>
    /// <param name="processes">The registry that owns the Git write.</param>
    /// <param name="gitFileName">The Git executable, normally <c>git</c>.</param>
    public GitBranchWriteTool(ShellProcessRegistry processes, string gitFileName = GitCommand.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitFileName);
        _git = new GitCommand(processes, gitFileName);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Create or delete a Git branch (git branch). Destructive and approval-gated; not available in Plan or Review. Branch names are validated and the checked-out branch cannot be deleted.",
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
        var op = (arguments.GetOptionalString("op") ?? string.Empty).Trim().ToLowerInvariant();
        if (op is not (Create or Delete))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"op must be '{Create}' or '{Delete}', not '{op}'.");
        }

        var timeout = TimeSpan.FromSeconds(
            arguments.GetOptionalInt("timeout_seconds", GitCommand.DefaultTimeoutSeconds, minimum: 1, maximum: 120));
        return await RunWriteAsync(toolCall, arguments.GetOptionalString("name"), op, context, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ToolResult> RunWriteAsync(
        ToolCall toolCall,
        string? rawName,
        string op,
        ToolInvocationContext context,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var name = rawName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"{op} requires a name.");
        }

        if (name[0] == '-' || name.AsSpan().IndexOfAny(Forbidden) >= 0 || name.Contains("..", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal)
            || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.'))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{name}' is not a valid branch name.");
        }

        string current;
        GitOutcome check;
        try
        {
            check = await _git.RunAsync(context.Workspace, ["check-ref-format", name], timeout, cancellationToken)
                .ConfigureAwait(false);
            var head = await _git.RunAsync(context.Workspace, ["rev-parse", "--abbrev-ref", "HEAD"], timeout, cancellationToken)
                .ConfigureAwait(false);
            current = head.ExitCode == 0 ? head.Output.Trim() : string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Failure(toolCall.Id, Name, ex.Message);
        }

        if (check.ExitCode != 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{name}' is not a valid branch name.");
        }

        if (op == Delete && string.Equals(name, current, StringComparison.Ordinal))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"refusing to delete the checked-out branch '{name}'.");
        }

        var command = op == Create
            ? new List<string> { "branch", name }
            : ["branch", "-d", name];

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
                string.Create(CultureInfo.InvariantCulture, $"git branch did not finish within {timeout.TotalSeconds:0} seconds."));
        }

        if (outcome.ExitCode != 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, GitStatusTool.Failure("branch", outcome));
        }

        var verb = op == Create ? "created" : "deleted";
        var summary = string.Create(CultureInfo.InvariantCulture, $"{verb} branch {name}");
        return ToolResult.Success(
            toolCall.Id,
            Name,
            summary,
            new ToolEffect(ToolEffectKind.CommandRun, string.Concat("git branch ", name)));
    }
}

