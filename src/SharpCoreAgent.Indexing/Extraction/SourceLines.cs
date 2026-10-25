namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// Splits text into lines without caring which terminator the file used.
/// </summary>
/// <remarks>
/// Shared by the extractor and the chunker so that both count lines the same way: a <c>\r\n</c>, a bare <c>\n</c>
/// and a bare <c>\r</c> each end exactly one line, and a trailing terminator does not invent a second one. If the
/// two disagreed, a chunker range and an extractor range would describe different text for the same file, and every
/// citation would be off by however much the file happens to disagree with itself.
/// </remarks>
internal static class SourceLines
{
    /// <summary>
    /// Splits text into lines.
    /// </summary>
    /// <param name="text">The text; an empty string is one empty line.</param>
    /// <returns>The lines, without their terminators.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static List<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<string>();
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\n' or '\r'))
            {
                continue;
            }

            lines.Add(text[start..index]);

            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        lines.Add(text[start..]);

        return lines;
    }
}
