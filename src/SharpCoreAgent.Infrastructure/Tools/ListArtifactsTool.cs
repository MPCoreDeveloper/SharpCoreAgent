using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Lists the files the MCP artefact sink has saved under <c>.sharpcoreagent/artifacts/</c>.
/// </summary>
/// <remarks>
/// The folder is deliberately excluded from the code index and from ordinary file listing, so a model that needs the
/// screenshot it just received has no way to discover it except the path in the tool result. This tool closes that gap
/// without making the folder index-visible: names, sizes and timestamps, capped and newest first.
/// </remarks>
public sealed class ListArtifactsTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "list_artifacts";

    /// <summary>Gets how many files one answer lists by default.</summary>
    public const int DefaultLimit = 100;

    /// <summary>Gets the largest list one answer may return.</summary>
    public const int MaxLimit = 500;

    private const string Schema =
        """{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":500,"description":"How many files to list, newest first. Defaults to 100."}},"required":[],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "List saved browser/MCP artefacts under .sharpcoreagent/artifacts/, newest first: path, size and write time. The folder is bounded at 256 MiB by the client and is not part of the code index. Use the exact path in a later read_file or view_image call.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.ReadOnly;

    /// <inheritdoc />
    public ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var limit = arguments.GetOptionalInt("limit", DefaultLimit, minimum: 1, maximum: MaxLimit);
        var folder = Path.Combine(context.Workspace.Root, McpArtefactSink.Folder.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(folder))
        {
            return ValueTask.FromResult(ToolResult.Success(
                toolCall.Id,
                Name,
                "artifacts: 0\nno artefacts have been saved",
                new ToolEffect(ToolEffectKind.Read, McpArtefactSink.Folder)));
        }

        var files = new List<FileInfo>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(path);
                if (file.LinkTarget is null)
                {
                    files.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(ToolResult.Failure(toolCall.Id, Name, $"the artefacts folder could not be read: {ex.Message}"));
        }

        files.Sort(static (left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
        var shown = files.Take(limit).ToArray();
        var text = new System.Text.StringBuilder();
        text.Append("artifacts: ").Append(files.Count.ToString(CultureInfo.InvariantCulture));
        if (files.Count > shown.Length)
        {
            text.Append(" (showing the newest ").Append(shown.Length.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        foreach (var file in shown)
        {
            text.Append('\n')
                .Append(context.Workspace.ToRelative(file.FullName))
                .Append(" · ")
                .Append(CodeIndexFormat.Size(file.Length))
                .Append(" · ")
                .Append(file.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        }

        return ValueTask.FromResult(ToolResult.Success(
            toolCall.Id,
            Name,
            text.ToString(),
            new ToolEffect(ToolEffectKind.Read, McpArtefactSink.Folder)));
    }
}
