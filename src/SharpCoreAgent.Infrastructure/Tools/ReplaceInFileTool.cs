using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Text;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Replaces one or more text fragments inside an existing file.
/// </summary>
/// <remarks>
/// This is the tool that makes an agent edit rather than rewrite. A whole-file write asks the model to
/// reproduce every line it did not intend to change, which is both expensive and the most common way an
/// agent destroys code it never read. A fragment replacement moves the risk to the match.
/// <para>
/// Matching is a ladder rather than a heuristic: verbatim first, then with the file's line endings, then
/// ignoring trailing whitespace, then ignoring the block's indentation. A fragment that is present exactly is
/// never matched loosely, and the result says which rung was needed. An ambiguous fragment is still refused:
/// tolerance is about whitespace, never about guessing which occurrence was meant.
/// </para>
/// <para>
/// Several fragments can be replaced in one call, in order, which is what keeps a three-line change from
/// costing three model round trips. The write is all-or-nothing: if any fragment cannot be matched, nothing is
/// written, so a failed call never leaves a file half-edited.
/// </para>
/// </remarks>
public sealed class ReplaceInFileTool : ITool, IToolPreview
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "replace_in_file";

    /// <summary>Gets the most fragments one call may replace.</summary>
    public const int MaxBlocks = 20;

    /// <summary>Gets the most lines one approval preview may show.</summary>
    public const int MaxPreviewLines = 160;

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string"},"find":{"type":"string","description":"Text to replace. Trailing whitespace and indentation may differ from the file; the result says which was forgiven."},"replace":{"type":"string","description":"Replacement text; an empty string deletes the match."},"all":{"type":"boolean","description":"Defaults to false. When true, every occurrence is replaced."},"edits":{"type":"array","description":"Several fragments to replace in one call, applied in order. Use this instead of find/replace; the whole call is refused if any fragment cannot be matched.","minItems":1,"maxItems":20,"items":{"type":"object","properties":{"find":{"type":"string"},"replace":{"type":"string"},"all":{"type":"boolean"}},"required":["find","replace"],"additionalProperties":false}}},"required":["path"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Replace fragments in an existing file, leaving the rest of the file byte-identical. Prefer this over write_file: it cannot drop lines the model did not intend to change. Pass find/replace for one fragment, or edits for several applied in order (the call is all-or-nothing). A fragment must be unique unless all=true; trailing whitespace and indentation differences are tolerated and reported.",
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
            Summary(edit),
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
        return read is { } edit ? Preview(edit) : null;
    }

    /// <summary>
    /// Reads the arguments, finds every fragment and computes the text the write would produce.
    /// </summary>
    /// <param name="toolCall">The call to read.</param>
    /// <param name="context">The workspace the path is resolved through.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The plan, or the message to report and whether it is a refusal. Both the write and the preview come
    /// from this one function, so what the developer is shown cannot differ from what would be written.
    /// </returns>
    private static async ValueTask<(EditRequest? Request, string? Error, bool Refused)> ReadAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var path = arguments.GetRequiredString("path");
        var blocks = Blocks(arguments);

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

        var text = original;
        var applied = new List<EditBlockResult>(blocks.Count);
        var snapshots = blocks.Count > 1 ? new List<string> { original } : null;

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var matches = FragmentFinder.Find(text, block.Find);
            var label = blocks.Count == 1
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"block {index + 1} of {blocks.Count}: ");

            if (matches.Count == 0)
            {
                return (
                    null,
                    string.Concat(
                        label,
                        $"the find text does not appear in '{relative}'. It was tried verbatim, then with the file's line endings, then without trailing whitespace, then without indentation; read the file and copy the fragment as it is."),
                    false);
            }

            if (matches.Count > 1 && !block.All)
            {
                return (
                    null,
                    string.Concat(
                        label,
                        $"the find text appears {matches.Count} times in '{relative}' ({Describe(matches[0].Kind)} match). Include more surrounding lines so the match is unique, or pass all=true to replace every occurrence."),
                    false);
            }

            applied.Add(new EditBlockResult(index, matches[0].Kind, matches.Count));
            text = Apply(text, matches, block);
            snapshots?.Add(text);
        }

        if (string.Equals(original, text, StringComparison.Ordinal))
        {
            return (
                null,
                $"the replacement leaves '{relative}' unchanged; find and replace are the same text.",
                false);
        }

        return (new EditRequest(fullPath, relative, original, text, applied, snapshots), null, false);
    }


    /// <summary>
    /// Reads the fragments to replace, from the single form or the list form.
    /// </summary>
    /// <param name="arguments">The validated argument reader.</param>
    /// <returns>One or more fragments, in the order they will be applied.</returns>
    /// <remarks>
    /// The two forms are distinct on purpose. One fragment is written as two top-level fields, which is the
    /// common case and the shortest call; several must be written as a list, so a model that forgets one of
    /// the top-level fields fails loudly instead of silently editing the wrong text.
    /// </remarks>
    /// <exception cref="ToolArgumentException">The two forms are mixed, or a fragment is malformed.</exception>
    private static List<EditBlock> Blocks(ToolArgumentReader arguments)
    {
        var edits = arguments.GetOptionalArray("edits");
        var hasSingle = arguments.Has("find") || arguments.Has("replace");

        if (edits.Count > 0 && hasSingle)
        {
            throw new ToolArgumentException(
                Name,
                "edits",
                "cannot be combined with find/replace; use find and replace for one fragment, or edits for several");
        }

        if (edits.Count == 0)
        {
            return
            [
                new EditBlock(
                    arguments.GetRequiredString("find"),
                    arguments.GetRequiredString("replace", allowEmpty: true),
                    arguments.GetOptionalBool("all", false)),
            ];
        }

        if (edits.Count > MaxBlocks)
        {
            throw new ToolArgumentException(
                Name,
                "edits",
                string.Create(CultureInfo.InvariantCulture, $"may list at most {MaxBlocks} fragments, not {edits.Count}"));
        }

        var blocks = new List<EditBlock>(edits.Count);
        for (var index = 0; index < edits.Count; index++)
        {
            var element = edits[index];
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ToolArgumentException(
                    Name,
                    string.Create(CultureInfo.InvariantCulture, $"edits[{index}]"),
                    "must be an object with find and replace");
            }

            blocks.Add(new EditBlock(
                ElementString(element, index, "find", allowEmpty: false),
                ElementString(element, index, "replace", allowEmpty: true),
                ElementBool(element, index, "all")));
        }

        return blocks;
    }

    private static string ElementString(JsonElement element, int index, string name, bool allowEmpty)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException(
                Name,
                string.Create(CultureInfo.InvariantCulture, $"edits[{index}].{name}"),
                "must be a string");
        }

        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && text.Length == 0)
        {
            throw new ToolArgumentException(
                Name,
                string.Create(CultureInfo.InvariantCulture, $"edits[{index}].{name}"),
                "must not be empty");
        }

        return text;
    }

    private static bool ElementBool(JsonElement element, int index, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ToolArgumentException(
                Name,
                string.Create(CultureInfo.InvariantCulture, $"edits[{index}].{name}"),
                "must be true or false"),
        };
    }


    /// <summary>
    /// Splices the replacement into every selected match.
    /// </summary>
    /// <param name="text">The text to change.</param>
    /// <param name="matches">Every match the fragment produced.</param>
    /// <param name="block">The fragment and its replacement.</param>
    /// <returns>The changed text.</returns>
    private static string Apply(string text, IReadOnlyList<FragmentMatch> matches, EditBlock block)
    {
        IReadOnlyList<FragmentMatch> selected = block.All ? matches : [matches[0]];
        var builder = new StringBuilder(text);

        // Backwards, so the offsets of the matches still to the left stay valid.
        for (var index = selected.Count - 1; index >= 0; index--)
        {
            var match = selected[index];
            builder.Remove(match.Index, match.Length).Insert(match.Index, match.Apply(block.Replace));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Describes what the call did, including how far down the ladder each fragment had to be matched.
    /// </summary>
    /// <param name="edit">The plan that was written.</param>
    /// <returns>One line for the model and the tool log.</returns>
    private static string Summary(EditRequest edit)
    {
        var occurrences = edit.Fragments.Sum(block => block.Occurrences);
        var head = edit.Fragments.Count == 1
            ? $"replaced {occurrences} occurrence(s) in {edit.RelativePath}"
            : $"replaced {edit.Fragments.Count} fragments ({occurrences} occurrence(s)) in {edit.RelativePath}";

        var notes = edit.Fragments
            .Where(block => block.Kind != FragmentMatchKind.Exact)
            .Select(block => edit.Fragments.Count == 1
                ? $"{Describe(block.Kind)} match"
                : $"block {block.Index + 1}: {Describe(block.Kind)}")
            .ToArray();

        return notes.Length == 0 ? head : string.Concat(head, " (", string.Join("; ", notes), ")");
    }

    /// <summary>
    /// Renders the change for the approval prompt.
    /// </summary>
    /// <param name="edit">The plan that would be written.</param>
    /// <returns>The diff, or an empty string when nothing would change.</returns>
    /// <remarks>
    /// One fragment shows the same diff it always has, with a note in front when the match needed the ladder.
    /// Several show one labelled hunk each, taken between the intermediate texts, so block two is shown
    /// against the file as block one leaves it rather than against the file as it was.
    /// </remarks>
    private static string Preview(EditRequest edit)
    {
        if (edit.Fragments.Count == 1)
        {
            var diff = UnifiedDiff.Create(edit.RelativePath, edit.Original, edit.Edited);
            if (diff.Length == 0)
            {
                return string.Empty;
            }

            var kind = edit.Fragments[0].Kind;
            return kind == FragmentMatchKind.Exact
                ? diff
                : string.Concat("# match: ", Describe(kind), "\n", diff);
        }

        var builder = new StringBuilder();
        for (var index = 0; index < edit.Fragments.Count; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"# block {index + 1}/{edit.Fragments.Count} ({Describe(edit.Fragments[index].Kind)})\n");
            var diff = UnifiedDiff.Create(edit.RelativePath, edit.Snapshots![index], edit.Snapshots[index + 1], maxLines: 40);
            builder.Append(diff.Length == 0 ? "(no change)\n" : diff);
        }

        return Bound(builder.ToString(), MaxPreviewLines);
    }

    private static string Bound(string text, int maxLines)
    {
        var lines = text.Split('\n');
        var kept = lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
        if (kept.Length <= maxLines)
        {
            return text;
        }

        var head = string.Join('\n', kept[..maxLines]);
        return string.Create(CultureInfo.InvariantCulture, $"{head}\n… {kept.Length - maxLines} more line(s)\n");
    }

    private static string Describe(FragmentMatchKind kind) => kind switch
    {
        FragmentMatchKind.LineEndings => "line endings",
        FragmentMatchKind.TrailingWhitespace => "trailing whitespace",
        FragmentMatchKind.Indentation => "indentation",
        _ => "exact",
    };


    /// <summary>
    /// Writes the text through a temporary file in the same directory, then moves it into place.
    /// </summary>
    /// <param name="fullPath">The file to replace.</param>
    /// <param name="content">The text to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when the file now holds the new text.</returns>
    /// <remarks>
    /// The move is the point: a write interrupted halfway through would leave a truncated source file, while a
    /// temporary file that never gets moved leaves the original exactly as it was.
    /// </remarks>
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

    /// <summary>One fragment to replace.</summary>
    /// <param name="Find">The text the model quoted.</param>
    /// <param name="Replace">What to put in its place.</param>
    /// <param name="All">Whether every occurrence is replaced.</param>
    private readonly record struct EditBlock(string Find, string Replace, bool All);

    /// <summary>What one fragment did.</summary>
    /// <param name="Index">Its position in the call, zero-based.</param>
    /// <param name="Kind">How it was matched.</param>
    /// <param name="Occurrences">How many occurrences were replaced.</param>
    private readonly record struct EditBlockResult(int Index, FragmentMatchKind Kind, int Occurrences);

    /// <summary>
    /// One invocation's plan: the text as it is, the text it would become, and how each fragment matched.
    /// </summary>
    /// <param name="FullPath">The absolute path to write.</param>
    /// <param name="RelativePath">The path shown to the model.</param>
    /// <param name="Original">The file's current text.</param>
    /// <param name="Edited">The text the write would produce.</param>
    /// <param name="Fragments">What each fragment did.</param>
    /// <param name="Snapshots">
    /// The intermediate texts from original to edited when there is more than one fragment; null otherwise,
    /// because a single fragment needs no intermediate state and a file's text is not worth copying for nothing.
    /// </param>
    private sealed record EditRequest(
        string FullPath,
        string RelativePath,
        string Original,
        string Edited,
        IReadOnlyList<EditBlockResult> Fragments,
        IReadOnlyList<string>? Snapshots);
}

