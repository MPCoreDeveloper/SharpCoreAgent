using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Returns a numbered window of a text file.
/// </summary>
/// <remarks>
/// Numbered lines exist for one reason: the model needs to be able to say "line 42" when it searches,
/// edits or reports, and a window <em>with</em> numbers is what makes a partial read usable. The window
/// is bounded in both lines and characters, and the answer says how to get the next one.
/// </remarks>
public sealed class ReadFileTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "read_file";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root."},"start_line":{"type":"integer","minimum":1},"line_count":{"type":"integer","minimum":1,"maximum":2000}},"required":["path"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Read a text file from the workspace. A relative path is resolved against the workspace root; an absolute path may also name a file in a read-only attachment that list_roots reports. Lines are numbered; read_file is always safe to call twice.",
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
        var path = arguments.GetRequiredString("path");
        var startLine = arguments.GetOptionalInt("start_line", 1, minimum: 1);
        var lineCount = arguments.GetOptionalInt("line_count", TextLimits.MaxFileLines, minimum: 1, maximum: TextLimits.MaxFileLines);

        if (!PathSafety.TryResolveRead(context.Workspace, path, out var fullPath, out var owner, out var error))
        {
            return ToolResult.Refused(toolCall.Id, Name, error!);
        }

        // The attachment that answered, when one did: a path shown relative to the tree the file lives in,
        // never an absolute one, because list_roots is the single result allowed to print those.
        var relative = (owner ?? context.Workspace).ToRelative(fullPath);
        if (Directory.Exists(fullPath))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' is a directory. Use list_directory to see what it contains.");
        }

        if (!File.Exists(fullPath))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"no file at '{relative}'");
        }

        if (TextLimits.LooksBinary(TextLimits.ReadHead(fullPath)))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' looks like a binary file (NUL byte in its first {TextLimits.BinarySniffBytes / 1024} KiB) and cannot be shown as text.");
        }

        if (new FileInfo(fullPath).Length == 0)
        {
            return ToolResult.Success(
                toolCall.Id,
                Name,
                "(empty file)",
                new ToolEffect(ToolEffectKind.Read, relative));
        }

        var builder = new StringBuilder();
        var number = 0;
        var shown = 0;
        var truncated = false;

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, TextLimits.Utf8NoBom, detectEncodingFromByteOrderMarks: true);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            number++;

            if (number < startLine)
            {
                continue;
            }

            if (shown == lineCount || builder.Length + line.Length + 8 > TextLimits.MaxFileReadChars)
            {
                truncated = true;
                break;
            }

            // Width 4 keeps the separator aligned for every file under 10 000 lines, which is nearly
            // all of them; a longer file simply widens the column.
            builder
                .Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(4, ' '))
                .Append('│')
                .Append(line)
                .Append('\n');
            shown++;
        }

        if (shown == 0)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' has {number.ToString(CultureInfo.InvariantCulture)} line(s); start_line {startLine.ToString(CultureInfo.InvariantCulture)} is past the end.");
        }

        if (truncated)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"[truncated: showed lines {startLine}-{startLine + shown - 1}; continue with start_line={startLine + shown}]");
        }

        return ToolResult.Success(
            toolCall.Id,
            Name,
            builder.ToString().TrimEnd('\n'),
            new ToolEffect(ToolEffectKind.Read, relative)) with
        { IsTruncated = truncated };
    }
}
