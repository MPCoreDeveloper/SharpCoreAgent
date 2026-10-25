using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Lists the workspace's files and directories, one entry per line.
/// </summary>
/// <remarks>
/// The model's cheapest way to learn the shape of a repository without spending a shell call. Output
/// is sorted and one level deep by default, because an unsorted recursive dump of a real project is
/// both huge and unreadable.
/// </remarks>
public sealed class ListDirectoryTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "list_directory";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Directory relative to the workspace root. Defaults to the root."},"recursive":{"type":"boolean","description":"Recurse into subdirectories."}},"required":[],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "List files and directories under a path in the workspace. Directories end with '/'. Use it to find out what exists before reading or writing.",
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
        var path = arguments.GetOptionalString("path") ?? ".";
        var recursive = arguments.GetOptionalBool("recursive", false);

        if (!PathSafety.TryResolve(context.Workspace, path, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Refused(toolCall.Id, Name, error!));
        }

        var relative = context.Workspace.ToRelative(fullPath);
        if (!Directory.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Failure(
                toolCall.Id,
                Name,
                File.Exists(fullPath)
                    ? $"'{relative}' is a file, not a directory. Use read_file to see its contents."
                    : $"no directory at '{relative}'"));
        }

        var lines = new List<string>();
        var truncated = false;

        foreach (var entry in Enumerate(fullPath, recursive, cancellationToken))
        {
            if (lines.Count == TextLimits.MaxDirectoryEntries)
            {
                truncated = true;
                break;
            }

            var shown = recursive ? context.Workspace.ToRelative(entry.Path) : entry.Name;
            lines.Add(entry.IsDirectory
                ? string.Create(CultureInfo.InvariantCulture, $"d {shown}/")
                : string.Create(CultureInfo.InvariantCulture, $"f {shown}"));
        }

        if (truncated)
        {
            lines.Add(TextLimits.TruncatedMarker);
        }

        var content = lines.Count == 0 ? "(no entries)" : string.Join('\n', lines);
        return ValueTask.FromResult(ToolResult.Success(
            toolCall.Id,
            Name,
            content,
            new ToolEffect(ToolEffectKind.Read, relative)) with
        { IsTruncated = truncated });
    }

    private static IEnumerable<(string Path, string Name, bool IsDirectory)> Enumerate(
        string root,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            var entries = new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var entry in entries)
            {
                var isDirectory = entry is DirectoryInfo;
                if (isDirectory && IgnoredPaths.IsIgnored(entry.Name))
                {
                    continue;
                }

                yield return (entry.FullName, entry.Name, isDirectory);

                // Recurse after yielding so a parent is always listed before its children, and skip
                // links so a self-referencing junction cannot make the walk run forever.
                if (isDirectory && recursive && entry.LinkTarget is null)
                {
                    pending.Push(entry.FullName);
                }
            }
        }
    }
}
