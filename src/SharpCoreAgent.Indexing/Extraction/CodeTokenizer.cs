using System.Text;

namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// Turns source text into the bag of words the lexical index and the embedder both work from.
/// </summary>
/// <remarks>
/// Code is not prose. A model asking for <c>run turn async</c> has to find <c>RunTurnAsync</c>, and it will never
/// type <c>runturnasync</c>, so the split between identifier words is the whole point of this class: camelCase,
/// PascalCase, snake_case, dots and digit boundaries all become separators, and everything is lowercased so the
/// same word matches whatever casing the file happened to use.
/// <para>
/// Splitting is done once, in <see cref="Split"/>, and is deliberately free of state: the same text yields the
/// same tokens in the same order on every call, in every process, so a rebuild produces an identical index.
/// </para>
/// <para>
/// Tokens shorter than <see cref="MinimumTokenLength"/> are noise — <c>a</c>, <c>x</c>, a lone digit — with one
/// exception: a single letter that is part of a longer identifier is a real component of the name
/// (<c>IModelClient</c> is an interface, not a typo), so it survives when its run produced other tokens.
/// </para>
/// </remarks>
internal static class CodeTokenizer
{
    /// <summary>Gets the length below which a token is dropped, in characters.</summary>
    internal const int MinimumTokenLength = 2;

    /// <summary>Gets the length beyond which a token is truncated, in characters.</summary>
    internal const int MaxTokenLength = 64;

    /// <summary>
    /// Gets the words that carry no signal in a query, so a question in English does not match every passage.
    /// </summary>
    /// <remarks>
    /// These are absent from the index on purpose: stop words are removed when a query is built, not when text is
    /// indexed, because BM25's inverse document frequency already discounts them on the document side and
    /// dropping them there would only make the document's term count lie.
    /// </remarks>
    private static readonly HashSet<string> QueryStopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "any", "are", "as", "at", "be", "but", "by", "can", "did", "do", "does", "for", "from",
        "has", "have", "how", "if", "in", "into", "is", "it", "its", "of", "on", "or", "our", "that", "the",
        "their", "then", "there", "these", "this", "to", "was", "we", "were", "what", "when", "where", "which",
        "who", "why", "will", "with", "would", "you", "your",
    };

    /// <summary>
    /// Splits text into lowercased words.
    /// </summary>
    /// <param name="text">The text to split; punctuation and whitespace are separators.</param>
    /// <returns>The tokens, in the order they appear, repetitions included.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<string>();
        var word = new StringBuilder(MaxTokenLength);
        var run = new List<string>(4);
        var index = 0;

        while (index < text.Length)
        {
            if (!char.IsLetterOrDigit(text[index]))
            {
                // Punctuation, whitespace, '_' and '.' all end a run: an identifier's separator is never a word.
                index++;
                continue;
            }

            var end = index;
            while (end < text.Length && char.IsLetterOrDigit(text[end]))
            {
                end++;
            }

            SplitRun(text.AsSpan(index, end - index), run, word);
            tokens.AddRange(run);
            run.Clear();

            index = end;
        }

        return tokens;
    }

    /// <summary>
    /// Produces the document text for the index: the file's words, space-joined.
    /// </summary>
    /// <param name="text">The source text.</param>
    /// <returns>The tokens joined by single spaces; empty when the text has no words.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string TokenizeForIndex(string text) => string.Join(' ', Split(text));


    /// <summary>
    /// Produces the query text for the lexical and vector legs: the same words, without stop words and without
    /// repetition.
    /// </summary>
    /// <param name="text">The query, as the model wrote it.</param>
    /// <returns>The tokens joined by single spaces; empty when the query carried no signal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string TokenizeForQuery(string text)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in Split(text))
        {
            if (QueryStopWords.Contains(token) || !seen.Add(token))
            {
                continue;
            }

            kept.Add(token);
        }

        return string.Join(' ', kept);
    }

    /// <summary>
    /// Splits one run of letters and digits at its internal word boundaries.
    /// </summary>
    /// <param name="run">The run, which contains no separators.</param>
    /// <param name="segments">Receives the segments, in order, after the length rule is applied.</param>
    /// <param name="buffer">A scratch buffer, reused so a large file does not allocate once per word.</param>
    private static void SplitRun(ReadOnlySpan<char> run, List<string> segments, StringBuilder buffer)
    {
        buffer.Clear();

        for (var index = 0; index < run.Length; index++)
        {
            if (index > 0 && IsWordBoundary(run, index))
            {
                segments.Add(buffer.ToString());
                buffer.Clear();
            }

            buffer.Append(char.ToLowerInvariant(run[index]));
        }

        segments.Add(buffer.ToString());

        // A lone short word is noise; the same short word inside a name ("IModelClient") is not.
        if (segments.Count == 1 && segments[0].Length < MinimumTokenLength)
        {
            segments.Clear();
        }
    }

    private static bool IsWordBoundary(ReadOnlySpan<char> run, int index)
    {
        var previous = run[index - 1];
        var current = run[index];

        if (char.IsDigit(previous) != char.IsDigit(current))
        {
            // "base64Encode" and "Turn2" split at the digit, because a number is its own word.
            return true;
        }

        if (!char.IsUpper(current))
        {
            return false;
        }

        if (char.IsLower(previous))
        {
            // camelCase and PascalCase: the capital starts the next word.
            return true;
        }

        // An acronym keeps its last capital with the word it begins: "HTTPServer" is "HTTP" + "Server", not
        // "HTTPS" + "erver".
        return index + 1 < run.Length && char.IsLower(run[index + 1]);
    }
}
