using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Creates or replaces a text file in the workspace.
/// </summary>
/// <remarks>
/// The write is done through a temporary file in the destination directory and a rename, so a cancelled
/// or failed write cannot leave the previous version truncated. Contents are UTF-8 with no byte-order
/// mark: an agent that silently adds one changes every diff and breaks tools that compare bytes.
/// </remarks>
public sealed class WriteFileTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "write_file";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"overwrite":{"type":"boolean","description":"Defaults to true. When false, an existing file is an error."}},"required":["path","content"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Create or replace a file in the workspace. Writes are confined to the workspace root; an attached root is read-only unless the session uses --approval allow, in which case an absolute path inside it is writable. Parent directories are created as needed. Content is written as UTF-8 without a BOM.",
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
        var content = arguments.GetRequiredString("content", allowEmpty: true);
        var overwrite = arguments.GetOptionalBool("overwrite", true);

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
                $"'{relative}' is a directory; write_file only writes files.");
        }

        var existed = File.Exists(fullPath);
        if (existed && !overwrite)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' already exists and overwrite is false; pass overwrite=true to replace it.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (context.Checkpoint is { } checkpoint &&
            await checkpoint.CaptureAsync(relative, fullPath, cancellationToken).ConfigureAwait(false) is { } captureError)
        {
            return ToolResult.Failure(toolCall.Id, Name, captureError);
        }

        var temporary = Path.Combine(
            string.IsNullOrEmpty(directory) ? context.Workspace.Root : directory,
            string.Create(CultureInfo.InvariantCulture, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp"));

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(temporary, content, TextLimits.Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return ToolResult.Failure(toolCall.Id, Name, $"could not write '{relative}': {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }

        var bytes = TextLimits.Utf8NoBom.GetByteCount(content);
        return ToolResult.Success(
            toolCall.Id,
            Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"wrote {bytes} bytes to {relative} ({(existed ? "overwritten" : "created")})"),
            new ToolEffect(existed ? ToolEffectKind.Modified : ToolEffectKind.Created, relative));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary file is already unreachable; leaving it behind is harmless and a failure
            // to clean up must not mask the write error the caller is about to see.
        }
    }
}
