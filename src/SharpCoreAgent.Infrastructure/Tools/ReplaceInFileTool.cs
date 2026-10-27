using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Text;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Replaces an exact text fragment inside an existing file.
/// </summary>
/// <remarks>
/// This is the tool that makes an agent edit rather than rewrite. A whole-file write asks the model to
/// reproduce every line it did not intend to change, which is both expensive and the most common way an
/// agent destroys code it never read. A fragment replacement moves the risk to the match: the text must
/// appear exactly, and when it appears more than once the call is refused instead of guessing which
/// occurrence was meant.
/// <para>
/// Matching is ordinal and whitespace-sensitive on purpose. A model that has to copy the surrounding
/// lines has read them, and a fuzzy match would let it change a line it never saw.
/// </para>
/// </remarks>
public sealed class ReplaceInFileTool : ITool, IToolPreview
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "replace_in_file";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string"},"find":{"type":"string","description":"Exact text to replace, including its indentation."},"replace":{"type":"string","description":"Replacement text; an empty string deletes the match."},"all":{"type":"boolean","description":"Defaults to false. When true, every occurrence is replaced."}},"required":["path","find","replace"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Replace an exact text fragment in an existing file, leaving the rest of the file byte-identical. Prefer this over write_file: it cannot drop lines the model did not intend to change. The find text must match exactly, including indentation, and must be unique unless all=true.",
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

        var (read, error, refused) = await ReadAsync(toolCall, context, cancellationToken).ConfigureAwait(false);
        if (read is not { } edit)
        {
            return refused
                ? ToolResult.Refused(toolCall.Id, Name, error!)
                : ToolResult.Failure(toolCall.Id, Name, error!);
        }

        if (edit.Occurrences == 0)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the find text does not appear in '{edit.RelativePath}'. Read the file first: the match is exact, so indentation, line endings and surrounding punctuation must all be identical."));
        }

        if (edit.Occurrences > 1 && !edit.All)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the find text appears {edit.Occurrences} times in '{edit.RelativePath}'. Include more surrounding lines so the match is unique, or pass all=true to replace every occurrence."));
        }

        if (string.Equals(edit.Original, edit.Edited, StringComparison.Ordinal))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"the replacement leaves '{edit.RelativePath}' unchanged; find and replace are the same text.");
        }

        if (!await WriteAsync(edit.FullPath, edit.Edited, cancellationToken).ConfigureAwait(false))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"could not write '{edit.RelativePath}'; the file was left untouched.");
        }

        return ToolResult.Success(
            toolCall.Id,
            Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"replaced {edit.Occurrences} occurrence(s) in {edit.RelativePath}"),
            new ToolEffect(ToolEffectKind.Modified, edit.RelativePath));
    }

    /// <inheritdoc />
    public async ValueTask<string?> DescribeChangeAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        var (read, _, _) = await ReadAsync(toolCall, context, cancellationToken).ConfigureAwait(false);
        if (read is not { } edit || edit.Occurrences == 0 ||
            string.Equals(edit.Original, edit.Edited, StringComparison.Ordinal))
        {
            return null;
        }

        return UnifiedDiff.Create(edit.RelativePath, edit.Original, edit.Edited);
    }

    private static async ValueTask<(EditRequest? Request, string? Error, bool Refused)> ReadAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var path = arguments.GetRequiredString("path");
        var find = arguments.GetRequiredString("find");
        var replace = arguments.GetRequiredString("replace", allowEmpty: true);
        var all = arguments.GetOptionalBool("all", false);

        if (!PathSafety.TryResolve(context.Workspace, path, out var fullPath, out var error))
        {
            return (null, error, true);
        }

        var relative = context.Workspace.ToRelative(fullPath);
        if (Directory.Exists(fullPath))
        {
            return (null, $"'{relative}' is a directory; {Name} only edits files.", false);
        }

        if (!File.Exists(fullPath))
        {
            return (null, $"'{relative}' does not exist. Use write_file to create a new file.", false);
        }

        string original;
        try
        {
            original = await File.ReadAllTextAsync(fullPath, TextLimits.Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"could not read '{relative}': {ex.Message}", false);
        }

        var occurrences = Count(original, find);
        var edited = occurrences == 0 ? original : Apply(original, find, replace, all);
        return (new EditRequest(fullPath, relative, original, edited, all, occurrences), null, false);
    }

    private static int Count(string text, string find)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += find.Length;
        }

        return count;
    }

    private static string Apply(string text, string find, string replace, bool all)
    {
        if (all)
        {
            return text.Replace(find, replace, StringComparison.Ordinal);
        }

        var index = text.IndexOf(find, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + find.Length));
    }

    private static async ValueTask<bool> WriteAsync(string fullPath, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var temporary = Path.Combine(
            string.IsNullOrEmpty(directory) ? Path.GetTempPath() : directory,
            string.Create(CultureInfo.InvariantCulture, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp"));

        try
        {
            await File.WriteAllTextAsync(temporary, content, TextLimits.Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return false;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary file is unreachable; failing to clean it up must not mask the write error.
        }
    }

    /// <summary>
    /// One invocation's inputs and the text they produce, computed once so the preview and the write
    /// cannot disagree about what the change is.
    /// </summary>
    private readonly record struct EditRequest(
        string FullPath,
        string RelativePath,
        string Original,
        string Edited,
        bool All,
        int Occurrences);
}
