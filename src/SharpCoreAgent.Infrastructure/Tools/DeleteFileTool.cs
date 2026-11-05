using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Removes one file from the workspace.
/// </summary>
/// <remarks>
/// Files only, never directories: a recursive delete is the single most destructive thing a coding
/// agent could be handed, and "delete this directory" is not a request this tool will interpret.
/// </remarks>
public sealed class DeleteFileTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "delete_file";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Delete a single file from the workspace. Directories cannot be deleted, and an attached root is read-only so nothing inside it can be deleted.",
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
        var path = arguments.GetRequiredString("path");

        if (!PathSafety.TryResolveWrite(
                context.Workspace,
                context.Approval,
                path,
                out var fullPath,
                out var owner,
                out var error))
        {
            return ToolResult.Refused(
                toolCall.Id,
                Name,
                PathSafety.ExplainWriteRefusal(context.Workspace, path, error!));
        }

        var relative = PathSafety.Relative(context.Workspace, owner, fullPath);
        if (Directory.Exists(fullPath))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' is a directory; delete_file only removes files.");
        }

        if (!File.Exists(fullPath))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"no file at '{relative}'");
        }

        if (context.Checkpoint is { } checkpoint &&
            await checkpoint.CaptureAsync(relative, fullPath, cancellationToken).ConfigureAwait(false) is { } captureError)
        {
            return ToolResult.Failure(toolCall.Id, Name, captureError);
        }

        try
        {
            File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"could not delete '{relative}': {ex.Message}");
        }

        return ToolResult.Success(
            toolCall.Id,
            Name,
            string.Create(CultureInfo.InvariantCulture, $"deleted {relative}"),
            new ToolEffect(ToolEffectKind.Deleted, relative));
    }
}
