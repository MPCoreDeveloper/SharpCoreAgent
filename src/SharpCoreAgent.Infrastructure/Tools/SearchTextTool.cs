using System.Globalization;
using System.IO.Enumeration;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Finds literal text across the workspace and reports where it is.
/// </summary>
/// <remarks>
/// Deliberately not a regex tool: a literal search is what a model uses nine times in ten, it cannot
/// be made to explode exponentially by a hostile pattern, and the result
/// (<c>path:line: text</c>) is directly usable as the next <c>read_file</c> argument.
/// </remarks>
public sealed class SearchTextTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "search_text";

    private const int MaxLineChars = 400;

    private const string Schema =
        """{"type":"object","properties":{"pattern":{"type":"string","description":"Literal text to find (case-insensitive)."},"glob":{"type":"string","description":"Filename pattern such as *.cs. Defaults to *."},"path":{"type":"string","description":"Directory to search, relative to the workspace root or absolute inside a read-only attachment. Defaults to the workspace root; a file path searches that one file."},"max_matches":{"type":"integer","minimum":1,"maximum":500}},"required":["pattern"],"additionalProperties":false}""";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Search the workspace, or an absolute path inside a read-only attachment that list_roots reports, for literal text. Returns path:line: text for each match. Skips .git, node_modules, bin, obj, .vs, .sharpcoreagent and binary or oversized files.",
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
        var pattern = arguments.GetRequiredString("pattern");
        var glob = arguments.GetOptionalString("glob") is { Length: > 0 } value ? value : "*";
        var path = arguments.GetOptionalString("path") is { Length: > 0 } searchIn ? searchIn : ".";
        var maxMatches = Math.Min(
            arguments.GetOptionalInt("max_matches", 100, minimum: 1, maximum: 500),
            TextLimits.MaxSearchMatches);

        if (!PathSafety.TryResolveRead(context.Workspace, path, out var fullPath, out var owner, out var error))
        {
            return ValueTask.FromResult(ToolResult.Refused(toolCall.Id, Name, error!));
        }

        // A hit in an attachment is reported relative to that attachment, so the path names the tree the
        // line came from rather than a prefix the model would have to reconstruct.
        var answered = owner ?? context.Workspace;
        var scope = answered.ToRelative(fullPath);
        var matches = new List<string>();
        var truncated = false;

        foreach (var file in Files(fullPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(file), ignoreCase: true) ||
                !TextLimits.IsSearchableText(file, out _))
            {
                continue;
            }

            if (TrySearchFile(answered, file, pattern, matches, maxMatches, cancellationToken))
            {
                truncated = true;
                break;
            }
        }

        if (matches.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Success(
                toolCall.Id,
                Name,
                $"no matches for '{pattern}'",
                new ToolEffect(ToolEffectKind.Read, scope)));
        }

        if (truncated)
        {
            matches.Add(TextLimits.TruncatedMarker);
        }

        return ValueTask.FromResult(ToolResult.Success(
            toolCall.Id,
            Name,
            string.Join('\n', matches),
            new ToolEffect(ToolEffectKind.Read, scope)) with
        { IsTruncated = truncated });
    }

    /// <summary>
    /// Names the files one invocation searches: the whole tree under a directory, or the file itself.
    /// </summary>
    /// <param name="fullPath">The resolved directory or file.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The files to try, in a stable order.</returns>
    /// <remarks>
    /// <c>search_text</c> accepts a file as well as a directory because the argument is a resolved path and
    /// refusing one of the two shapes it can name would be a rule the model has to remember for no gain.
    /// </remarks>
    private static IEnumerable<string> Files(string fullPath, CancellationToken cancellationToken) =>
        File.Exists(fullPath) ? [fullPath] : EnumerateFiles(fullPath, cancellationToken);

    /// <summary>
    /// Searches one file, appending matches and reporting whether the budget ran out.
    /// </summary>
    /// <param name="answered">The root that answered the path, so matches print a path relative to it.</param>
    /// <param name="file">The file to read.</param>
    /// <param name="pattern">The literal text to find.</param>
    /// <param name="matches">The matches collected so far.</param>
    /// <param name="maxMatches">The ceiling this invocation was given.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when the budget ran out mid-file.</returns>
    private static bool TrySearchFile(
        Workspace answered,
        string file,
        string pattern,
        List<string> matches,
        int maxMatches,
        CancellationToken cancellationToken)
    {
        var relative = answered.ToRelative(file);

        try
        {
            using var reader = new StreamReader(file, TextLimits.Utf8NoBom, detectEncodingFromByteOrderMarks: true);
            var number = 0;

            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                number++;

                if (line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (matches.Count == maxMatches)
                {
                    return true;
                }

                var text = line.TrimEnd();
                if (text.Length > MaxLineChars)
                {
                    text = text[..MaxLineChars] + "…";
                }

                matches.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{relative}:{number}: {text}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that vanished or cannot be read mid-scan is not a reason to fail the search: the
            // model asked what exists across the workspace, and this file is not an answer.
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            FileSystemInfo[] entries;
            try
            {
                entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(entries, (left, right) => string.CompareOrdinal(left.Name, right.Name));

            // Pushed in reverse so the stack pops them in sorted order.
            for (var index = entries.Length - 1; index >= 0; index--)
            {
                if (entries[index] is DirectoryInfo child &&
                    !IgnoredPaths.IsIgnored(child.Name) &&
                    child.LinkTarget is null)
                {
                    pending.Push(child.FullName);
                }
            }

            foreach (var entry in entries.Where(entry => entry is FileInfo))
            {
                yield return entry.FullName;
            }
        }
    }
}
