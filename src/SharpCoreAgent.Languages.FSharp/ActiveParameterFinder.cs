namespace SharpCoreAgent.Languages.FSharp;

/// <summary>
/// Works out which argument a position is inside, from the call site's text.
/// </summary>
/// <remarks>
/// The checker exposes no argument index, so it is derived here: find the innermost unclosed parenthesis before the
/// position, then count the commas at its own nesting level. Strings are skipped so a comma inside a literal is not
/// an argument separator, and nested calls are skipped so <c>f(g(a, b), c)</c> answers 1 at <c>c</c>.
/// <para>
/// A position that is not inside a call answers <c>-1</c> rather than 0. That is the difference between "the first
/// argument" and "no argument", and a signature renderer that confused them would highlight the wrong thing.
/// </para>
/// </remarks>
internal static class ActiveParameterFinder
{
    /// <summary>
    /// Finds the 0-based index of the argument at a position.
    /// </summary>
    /// <param name="lineText">The line as written.</param>
    /// <param name="column">The 0-based position on that line.</param>
    /// <returns>The argument index, or <c>-1</c> when the position is not inside a call's arguments.</returns>
    public static int Find(string lineText, int column)
    {
        if (string.IsNullOrEmpty(lineText))
        {
            return -1;
        }

        var end = Math.Clamp(column, 0, lineText.Length);
        var open = OpeningParenthesis(lineText, end);
        if (open < 0)
        {
            return -1;
        }

        var arguments = 0;
        var nesting = 0;
        var insideString = false;
        var index = open + 1;
        while (index < end)
        {
            var character = lineText[index];
            if (insideString)
            {
                if (character == '\\')
                {
                    index += 2;
                    continue;
                }

                if (character == '"')
                {
                    insideString = false;
                }

                index++;
                continue;
            }

            switch (character)
            {
                case '"':
                    insideString = true;
                    break;
                case '(' or '[' or '{':
                    nesting++;
                    break;
                case ')' or ']' or '}':
                    nesting--;
                    break;
                case ',' when nesting == 0:
                    arguments++;
                    break;
                default:
                    break;
            }

            index++;
        }

        return arguments;
    }

    private static int OpeningParenthesis(string lineText, int end)
    {
        var depth = 0;
        for (var index = end - 1; index >= 0; index--)
        {
            switch (lineText[index])
            {
                case ')':
                    depth++;
                    break;
                case '(':
                    if (depth == 0)
                    {
                        return index;
                    }

                    depth--;
                    break;
                default:
                    break;
            }
        }

        return -1;
    }
}
