using System.Globalization;
using System.Text;

namespace SharpCoreAgent.Domain.Text;

/// <summary>
/// Renders the difference between two versions of a text file as a unified diff.
/// </summary>
/// <remarks>
/// The renderer is built for one job: showing a developer what an edit would do before it is approved.
/// It therefore describes the changed region rather than performing a full LCS diff -- it trims the
/// lines both versions share at the top and the bottom, renders that one region with context, and stops
/// at a line budget. A file whose middle was edited produces the same hunk a full algorithm would; a
/// file with many scattered edits produces one larger hunk, which is exactly the honest thing to show
/// before replacing text everywhere. The output is bounded in time and memory, so a preview of a
/// 10,000-line file costs one pass over the lines rather than a quadratic table.
/// </remarks>
public static class UnifiedDiff
{
    /// <summary>
    /// Renders the change between two texts.
    /// </summary>
    /// <param name="path">The workspace-relative path shown in the <c>---</c> and <c>+++</c> headers.</param>
    /// <param name="oldText">The text as it is on disk.</param>
    /// <param name="newText">The text as it would be.</param>
    /// <param name="contextLines">The unchanged lines shown on each side; zero renders only the change.</param>
    /// <param name="maxLines">The ceiling on rendered lines; the tail is replaced by a count when exceeded.</param>
    /// <returns>The diff, or an empty string when the two texts are identical.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A line count is negative or too small.</exception>
    public static string Create(
        string path,
        string oldText,
        string newText,
        int contextLines = 2,
        int maxLines = 60)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 4);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix &&
               string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        return Render(path, oldLines, newLines, prefix, suffix, contextLines, maxLines);
    }
    private static string Render(
        string path,
        List<string> oldLines,
        List<string> newLines,
        int prefix,
        int suffix,
        int contextLines,
        int maxLines)
    {
        var oldChanged = oldLines.Count - prefix - suffix;
        var newChanged = newLines.Count - prefix - suffix;
        // Line endings are normalised before comparison, so a text that differs only in its terminators
        // has no line-level change to show. Returning headers with an empty hunk would suggest otherwise.
        if (oldChanged == 0 && newChanged == 0)
        {
            return string.Empty;
        }

        var leading = Math.Min(contextLines, prefix);
        var trailing = Math.Min(contextLines, suffix);

        var body = new StringBuilder();
        var rendered = 0;

        for (var index = prefix - leading; index < prefix; index++)
        {
            AppendLine(body, ' ', oldLines[index], ref rendered);
        }

        // The removed side first, then the added one: that is the order every diff tool uses and the
        // order a reader needs to see a replacement as a replacement rather than as two lists.
        for (var index = 0; index < oldChanged && rendered < maxLines; index++)
        {
            AppendLine(body, '-', oldLines[prefix + index], ref rendered);
        }

        for (var index = 0; index < newChanged && rendered < maxLines; index++)
        {
            AppendLine(body, '+', newLines[prefix + index], ref rendered);
        }

        for (var index = 0; index < trailing && rendered < maxLines; index++)
        {
            AppendLine(body, ' ', oldLines[prefix + oldChanged + index], ref rendered);
        }

        var total = leading + oldChanged + newChanged + trailing;
        if (rendered < total)
        {
            body.Append(CultureInfo.InvariantCulture, $"… {total - rendered} more line(s)\n");
        }

        var header = new StringBuilder();
        header.Append(CultureInfo.InvariantCulture, $"--- a/{path}\n");
        header.Append(CultureInfo.InvariantCulture, $"+++ b/{path}\n");
        header.Append(CultureInfo.InvariantCulture, $"@@ -{Range(prefix, oldChanged)} +{Range(prefix, newChanged)} @@\n");
        header.Append(body);

        return header.ToString();
    }

    private static string Range(int prefix, int count)
    {
        // A zero-length range points at the line before the insertion, which is what `-0,0` means for an
        // insertion at the top of a file. Reporting `-1,0` would name a line that does not exist.
        var start = count == 0 ? prefix : prefix + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{start},{count}");
    }

    private static void AppendLine(StringBuilder builder, char marker, string line, ref int rendered)
    {
        builder.Append(marker).Append(line).Append('\n');
        rendered++;
    }

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = new List<string>(normalized.Split('\n'));

        // A trailing newline is the normal state of a text file, not an empty last line. Dropping the
        // artefact keeps a one-line change from being reported as a change plus an appended blank line.
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
