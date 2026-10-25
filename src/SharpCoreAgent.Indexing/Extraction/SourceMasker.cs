namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// Blanks out comments and string literals while keeping every line where it was, so the code that remains can be
/// pattern-matched without matching prose.
/// </summary>
/// <remarks>
/// The whole point is <c>// see AlsoDoThis()</c> and <c>"call RefreshAsync()"</c>: a call detector that reads raw
/// text invents edges from sentences, and an invented edge is worse than a missing one because it survives into the
/// graph and drags unrelated passages into every answer.
/// <para>
/// Masking preserves offsets and line breaks — masked characters become spaces, newlines stay — so a match found in
/// the masked text is at the same line as the original and a range computed from it can be reported to a reader.
/// </para>
/// <para>
/// This is a lexer's approximation, not a lexer: interpolated expressions inside strings are masked with the rest
/// of the string, template languages escape differently, and a stray apostrophe in a comment is harmless because
/// comments are consumed first. Everything it gets wrong costs a missing relation, never a wrong line range.
/// </para>
/// </remarks>
internal static class SourceMasker
{
    /// <summary>
    /// Replaces comments and string literals with spaces.
    /// </summary>
    /// <param name="text">The source text.</param>
    /// <returns>A string of the same length, with the same line breaks, holding only code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var masked = text.ToCharArray();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '/' && Peek(text, index + 1) == '/')
            {
                index = BlankToEndOfLine(text, masked, index);
                continue;
            }

            if (current == '/' && Peek(text, index + 1) == '*')
            {
                index = BlankBlockComment(text, masked, index);
                continue;
            }

            if (current == '#')
            {
                // A C# preprocessor line and a Python comment both end at the newline; masking either is right.
                index = BlankToEndOfLine(text, masked, index);
                continue;
            }

            if (current == '"' || current == '\'')
            {
                index = BlankString(text, masked, index);
                continue;
            }

            index++;
        }

        return new string(masked);
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    private static int BlankToEndOfLine(string text, char[] masked, int index)
    {
        while (index < text.Length && text[index] is not ('\n' or '\r'))
        {
            masked[index++] = ' ';
        }

        return index;
    }

    private static int BlankBlockComment(string text, char[] masked, int index)
    {
        masked[index++] = ' ';
        masked[index++] = ' ';

        while (index < text.Length)
        {
            if (text[index] == '*' && Peek(text, index + 1) == '/')
            {
                masked[index] = ' ';
                masked[index + 1] = ' ';
                return index + 2;
            }

            if (text[index] is not ('\n' or '\r'))
            {
                masked[index] = ' ';
            }

            index++;
        }

        return index;
    }

    private static int BlankString(string text, char[] masked, int index)
    {
        var quote = text[index];

        // A triple quote opens a block string (C# raw strings, Python docstrings); a single one closes at the next
        // matching quote that is not escaped.
        if (Peek(text, index + 1) == quote && Peek(text, index + 2) == quote)
        {
            masked[index] = masked[index + 1] = masked[index + 2] = ' ';
            return BlankBlockString(text, masked, index + 3, quote);
        }

        masked[index++] = ' ';
        return BlankSingleString(text, masked, index, quote);
    }

    /// <summary>
    /// Blanks a block string, up to and including its closing triple quote.
    /// </summary>
    /// <param name="text">The whole file.</param>
    /// <param name="masked">The mask being written.</param>
    /// <param name="index">The first character after the opening triple quote.</param>
    /// <param name="quote">The quote character that opened the block.</param>
    /// <returns>The index just past what was consumed.</returns>
    private static int BlankBlockString(string text, char[] masked, int index, char quote)
    {
        while (index < text.Length)
        {
            if (text[index] == quote && Peek(text, index + 1) == quote && Peek(text, index + 2) == quote)
            {
                masked[index] = masked[index + 1] = masked[index + 2] = ' ';
                return index + 3;
            }

            if (text[index] is not ('\n' or '\r'))
            {
                masked[index] = ' ';
            }

            index++;
        }

        return index;
    }

    /// <summary>
    /// Blanks a single-quoted string, up to and including its closing quote.
    /// </summary>
    /// <param name="text">The whole file.</param>
    /// <param name="masked">The mask being written.</param>
    /// <param name="index">The first character after the opening quote.</param>
    /// <param name="quote">The quote character that opened the literal.</param>
    /// <returns>The index just past what was consumed.</returns>
    private static int BlankSingleString(string text, char[] masked, int index, char quote)
    {
        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\')
            {
                // An escape hides the closing quote, so it is consumed with its payload.
                masked[index++] = ' ';

                if (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    masked[index++] = ' ';
                }

                continue;
            }

            if (current == quote)
            {
                masked[index++] = ' ';
                return index;
            }

            if (current is '\n' or '\r')
            {
                // An unterminated literal must not eat the rest of the file.
                return index;
            }

            masked[index++] = ' ';
        }

        return index;
    }
}
