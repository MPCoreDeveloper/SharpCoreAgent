namespace SharpCoreAgent.Domain.Text;

/// <summary>
/// How a fragment was located inside a file.
/// </summary>
/// <remarks>
/// The order of the members is the order the matcher tries them, strictest first. A caller therefore
/// reads the kind as a statement about how much was forgiven, and the tool reports it so a developer can
/// see when an edit needed more than an exact match.
/// </remarks>
public enum FragmentMatchKind
{
    /// <summary>The text appeared byte for byte.</summary>
    Exact,

    /// <summary>The text appeared once its line terminators matched the file's.</summary>
    LineEndings,

    /// <summary>The text appeared once spaces and tabs at the end of its lines were ignored.</summary>
    TrailingWhitespace,

    /// <summary>The text appeared once the block's common leading indentation was ignored.</summary>
    Indentation,
}

/// <summary>
/// One located occurrence of a fragment: where it is, how long it is, and what was forgiven to find it.
/// </summary>
/// <remarks>
/// The match also knows how to place the replacement, which is not a detail a caller should have to get
/// right: a block found by ignoring indentation must be written back at the indentation the file uses,
/// and a block found by ignoring line endings must not introduce the other platform's terminators.
/// <see cref="Apply"/> is the only supported way to turn a replacement into text.
/// </remarks>
public sealed class FragmentMatch
{
    private readonly string _lineEnding;
    private readonly string _findIndent;
    private readonly string _blockIndent;

    private FragmentMatch(
        int index,
        int length,
        FragmentMatchKind kind,
        string lineEnding,
        string findIndent,
        string blockIndent)
    {
        Index = index;
        Length = length;
        Kind = kind;
        _lineEnding = lineEnding;
        _findIndent = findIndent;
        _blockIndent = blockIndent;
    }

    /// <summary>Gets the zero-based character offset of the match.</summary>
    public int Index { get; }

    /// <summary>Gets the match length in characters.</summary>
    public int Length { get; }

    /// <summary>Gets how the match was found.</summary>
    public FragmentMatchKind Kind { get; }

    /// <summary>
    /// Prepares a replacement for this location.
    /// </summary>
    /// <param name="replacement">The text the caller wants in place of the match.</param>
    /// <returns>
    /// The text as it should be spliced: re-indented and re-terminated for a tolerant match, and
    /// unchanged for an exact one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is null.</exception>
    public string Apply(string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        return Kind switch
        {
            FragmentMatchKind.LineEndings => Express(replacement, _lineEnding),
            FragmentMatchKind.TrailingWhitespace => Express(replacement, _lineEnding),
            FragmentMatchKind.Indentation => Express(Reindent(replacement), _lineEnding),
            _ => replacement,
        };
    }

    /// <summary>Rewrites every terminator in a text as the file writes them.</summary>
    /// <param name="text">The text to rewrite.</param>
    /// <param name="lineEnding">The terminator to use: <c>"\n"</c> or <c>"\r\n"</c>.</param>
    /// <returns>The text with one consistent terminator.</returns>
    internal static string Express(string text, string lineEnding)
    {
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return string.Equals(lineEnding, "\n", StringComparison.Ordinal)
            ? normalised
            : normalised.Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    /// <summary>Creates a match. The constructor is private so a match always carries a usable ending.</summary>
    /// <param name="index">The offset.</param>
    /// <param name="length">The length.</param>
    /// <param name="kind">How it was found.</param>
    /// <param name="lineEnding">The file's terminator.</param>
    /// <param name="findIndent">The fragment's common indentation.</param>
    /// <param name="blockIndent">The matched block's indentation.</param>
    /// <returns>The match.</returns>
    internal static FragmentMatch Create(
        int index,
        int length,
        FragmentMatchKind kind,
        string lineEnding,
        string findIndent,
        string blockIndent) =>
        new(index, length, kind, lineEnding, findIndent, blockIndent);

    private string Reindent(string replacement)
    {
        if (string.Equals(_findIndent, _blockIndent, StringComparison.Ordinal))
        {
            return replacement;
        }

        var lines = Express(replacement, "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                // A blank line has no indentation to correct, and inventing some would add whitespace
                // to a file the model did not ask to change.
                continue;
            }

            if (_findIndent.Length > 0 && line.StartsWith(_findIndent, StringComparison.Ordinal))
            {
                lines[index] = string.Concat(_blockIndent, line.AsSpan(_findIndent.Length));
            }
            else if (_findIndent.Length == 0)
            {
                lines[index] = string.Concat(_blockIndent, line);
            }
        }

        return string.Join('\n', lines);
    }
}

/// <summary>
/// Finds a text fragment in a file, tolerating the differences a model introduces when it copies code.
/// </summary>
/// <remarks>
/// Matching is a ladder, not a heuristic: exact first, then the same text with the file's line endings,
/// then the same lines with trailing whitespace ignored, then the same lines with their common
/// indentation ignored. Each rung is only consulted when the previous one found nothing, so a fragment
/// present verbatim is never matched loosely, and <see cref="FragmentMatch.Kind"/> says exactly how much
/// was forgiven.
/// <para>
/// Every rung returns <em>all</em> of its matches rather than the first. That is what keeps the edit path
/// safe: the caller can still refuse an ambiguous fragment instead of guessing which occurrence the model
/// meant, however the fragment was found.
/// </para>
/// <para>
/// Nothing here is a similarity score. A line either matches at the current rung or it does not, so the
/// result is reproducible and a reviewer can say which rule accepted an edit. Content that merely
/// resembles the fragment is not matched at all.
/// </para>
/// </remarks>
public static class FragmentFinder
{
    private static readonly char[] HorizontalWhitespace = [' ', '\t'];

    /// <summary>
    /// Locates every occurrence of a fragment.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="find">The fragment to find.</param>
    /// <returns>
    /// Every match found at the strictest rung that found any, in file order; empty when no rung found the
    /// fragment. Matches at one rung never overlap.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<FragmentMatch> Find(string text, string find)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(find);

        if (find.Length == 0)
        {
            return [];
        }

        var ending = DominantEnding(text);
        var exact = Scan(text, find, FragmentMatchKind.Exact, ending);
        if (exact.Count > 0 || string.IsNullOrWhiteSpace(find))
        {
            return exact;
        }

        // A fragment typed with the other platform's terminators is the most common mismatch there is,
        // and the only one where the text is otherwise byte-identical.
        var expressed = FragmentMatch.Express(find, ending);
        if (!string.Equals(expressed, find, StringComparison.Ordinal))
        {
            var terminated = Scan(text, expressed, FragmentMatchKind.LineEndings, ending);
            if (terminated.Count > 0)
            {
                return terminated;
            }
        }

        var trailing = MatchLines(text, find, FragmentMatchKind.TrailingWhitespace);
        return trailing.Count > 0 ? trailing : MatchLines(text, find, FragmentMatchKind.Indentation);
    }

    /// <summary>Reads the line terminator a file uses.</summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns><c>"\r\n"</c> when carriage returns predominate, otherwise <c>"\n"</c>.</returns>
    private static string DominantEnding(string text)
    {
        var carriageReturns = 0;
        var lineFeeds = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lineFeeds++;
            }
            else if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                carriageReturns++;
            }
        }

        // CRLF wins only when it is not outnumbered by bare line feeds, so a file that mixes both is read
        // as the platform it predominantly is rather than as whichever ending came first.
        return carriageReturns > 0 && carriageReturns >= lineFeeds - carriageReturns ? "\r\n" : "\n";
    }
    private static List<FragmentMatch> Scan(string text, string find, FragmentMatchKind kind, string ending)
    {
        var matches = new List<FragmentMatch>();
        var index = 0;
        while ((index = text.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
        {
            matches.Add(FragmentMatch.Create(index, find.Length, kind, ending, string.Empty, string.Empty));
            index += find.Length;
        }

        return matches;
    }

    private static List<FragmentMatch> MatchLines(string text, string find, FragmentMatchKind kind)
    {
        var matches = new List<FragmentMatch>();
        var spans = LineSpans(text);
        var findLines = SplitLines(find);
        if (findLines.Length == 0 || findLines.Length > spans.Count)
        {
            return matches;
        }

        var ending = DominantEnding(text);
        var findIndent = CommonIndent(findLines);
        var relative = StripIndent(findLines, findIndent);
        var endsWithTerminator = find.EndsWith('\n') || find.EndsWith('\r');

        var start = 0;
        while (start + findLines.Length <= spans.Count)
        {
            if (!BlockMatches(text, spans, start, findLines, relative, kind))
            {
                start++;
                continue;
            }

            var first = spans[start];
            var last = spans[start + findLines.Length - 1];
            var end = last.Start + last.ContentLength + (endsWithTerminator ? last.TerminatorLength : 0);

            matches.Add(FragmentMatch.Create(
                first.Start,
                end - first.Start,
                kind,
                ending,
                findIndent,
                LeadingWhitespace(text, first)));

            // Non-overlapping, exactly like the ordinal scan: "aa" in "aaa" is one match, not two.
            start += findLines.Length;
        }

        return matches;
    }


    private static bool BlockMatches(
        string text,
        List<LineSpan> spans,
        int start,
        string[] findLines,
        string[] relative,
        FragmentMatchKind kind)
    {
        for (var offset = 0; offset < findLines.Length; offset++)
        {
            var span = spans[start + offset];
            var line = text.AsSpan(span.Start, span.ContentLength);

            if (kind == FragmentMatchKind.TrailingWhitespace)
            {
                if (!line.TrimEnd(HorizontalWhitespace).SequenceEqual(findLines[offset].AsSpan().TrimEnd(HorizontalWhitespace)))
                {
                    return false;
                }

                continue;
            }

            // The last rung trims both ends. The fragment's indentation and the file's are then
            // irrelevant, which is the point: only the text has to agree.
            if (!line.Trim(HorizontalWhitespace).SequenceEqual(relative[offset].AsSpan().Trim(HorizontalWhitespace)))
            {
                return false;
            }
        }

        return true;
    }


    private static string[] StripIndent(string[] lines, string indent)
    {
        var stripped = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            stripped[index] = indent.Length > 0 && line.StartsWith(indent, StringComparison.Ordinal)
                ? line[indent.Length..]
                : line;
        }

        return stripped;
    }

    private static string CommonIndent(string[] lines)
    {
        var indent = string.Empty;
        var found = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var leading = LeadingWhitespace(line);
            if (!found)
            {
                indent = leading;
                found = true;
                continue;
            }

            var shared = 0;
            while (shared < indent.Length && shared < leading.Length && indent[shared] == leading[shared])
            {
                shared++;
            }

            indent = indent[..shared];
            if (indent.Length == 0)
            {
                return string.Empty;
            }
        }

        return indent;
    }

    private static string LeadingWhitespace(string line)
    {
        var length = 0;
        while (length < line.Length && char.IsWhiteSpace(line[length]))
        {
            length++;
        }

        return line[..length];
    }

    private static string LeadingWhitespace(string text, LineSpan span)
    {
        var length = 0;
        while (length < span.ContentLength && char.IsWhiteSpace(text[span.Start + length]))
        {
            length++;
        }

        return text.Substring(span.Start, length);
    }

    private static string[] SplitLines(string text)
    {
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalised.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static List<LineSpan> LineSpans(string text)
    {
        var spans = new List<LineSpan>();
        var start = 0;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];
            if (character != '\r' && character != '\n')
            {
                index++;
                continue;
            }

            var terminator = character == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            spans.Add(new LineSpan(start, index - start, terminator));
            index += terminator;
            start = index;
        }

        // A trailing terminator ends the last line rather than starting an empty one, which is the rule
        // <see cref="UnifiedDiff"/> uses too, so a preview and a match agree about line numbers.
        if (start < text.Length)
        {
            spans.Add(new LineSpan(start, text.Length - start, 0));
        }

        return spans;
    }

    /// <summary>One line: where its content starts, how long it is and how its terminator is spelled.</summary>
    /// <param name="Start">The offset of the first character.</param>
    /// <param name="ContentLength">The length without the terminator.</param>
    /// <param name="TerminatorLength">Zero at the end of a file that has no final newline.</param>
    private readonly record struct LineSpan(int Start, int ContentLength, int TerminatorLength);


}
