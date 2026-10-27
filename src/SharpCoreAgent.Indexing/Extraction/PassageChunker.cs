namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// Cuts a file into passages that a reader can open and a model can cite.
/// </summary>
/// <remarks>
/// Three rules, applied in this order: end a passage where the next declaration starts, otherwise at the last blank
/// line inside the caps, otherwise wherever the caps force a split. The result never starts or ends mid-line, never
/// overlaps its neighbour, and tiles the file — every line belongs to exactly one passage, which is what makes "this
/// passage changed, re-index the file" a cheap comparison instead of a guess.
/// <para>
/// Caps are 40 lines and 1 600 characters. The first is about reading: a passage is what a tool hands back, and 40
/// lines is a screen. The second is about ranking: BM25 scores a bag of words, and a passage too long to have a
/// subject scores everything and matches nothing well.
/// </para>
/// <para>
/// Declaration alignment pays for itself twice. A window that starts mid-statement produces a citation that begins
/// with half an expression, and the model reading it spends a turn working out what it is part of. And a passage that
/// holds more than one declaration can only be named after one of them, which is how the exact-symbol boost in
/// ranking silently stops working: ending at the next declaration is not a nicety, it is the input the ranker needs.
/// </para>
/// </remarks>
internal static class PassageChunker
{
    /// <summary>Gets the most lines one passage may span.</summary>
    internal const int MaxLines = 40;

    /// <summary>Gets the most characters one passage may hold.</summary>
    internal const int MaxChars = 1600;

    /// <summary>Gets how far into a passage a declaration may start and still name it: a namespace line, a few
    /// usings, the type header.</summary>
    private const int LeadingHeaderLines = 4;

    /// <summary>
    /// Chunks one file.
    /// </summary>
    /// <param name="text">The file's text, exactly as read.</param>
    /// <param name="symbols">The declarations the extractor found, used as split points.</param>
    /// <param name="cancellationToken">Cancels between passages.</param>
    /// <returns>The passages in file order; empty for a file with nothing in it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="symbols"/> is null.</exception>
    public static List<Passage> Chunk(
        string text,
        IReadOnlyList<ExtractedSymbol> symbols,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(symbols);

        var lines = SourceLines.Split(text);

        // A file that ends with a newline has a last, empty line; it is a terminator, not content, and counting it
        // would put a blank line in the final passage of every file in the repository.
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return [];
        }

        var declarations = DeclarationsByLine(symbols);
        var passages = new List<Passage>();
        var start = 0;

        while (start < lines.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var end = Window(lines, start, declarations);
            var declaration = DeclarationsAt(declarations, start, end);

            var kind = PassageKinds.Block;
            if (declaration is not null)
            {
                kind = declaration.IsContainer ? PassageKinds.Type : PassageKinds.Member;
            }

            passages.Add(new Passage
            {
                StartLine = start + 1,
                EndLine = end + 1,
                Kind = kind,

                // Trimmed at the edges so the passage is the declaration rather than the indentation in front of
                // it, and so `verify` hashes exactly the text a reader is shown. This is the chunker's choice, not
                // a workaround: the engine used to strip surrounding whitespace off a bound TEXT by itself — which
                // is why trimming was introduced — and 2.1.2 round-trips it (ProseRoundTripTests covers the shape).
                Text = string.Join('\n', lines.Skip(start).Take(end - start + 1)).Trim(),
                SymbolKey = declaration?.Key,
                SymbolName = declaration?.QualifiedName,
            });

            start = end + 1;
        }

        return passages;
    }

    /// <summary>
    /// Finds the last line of the passage that starts at <paramref name="start"/>.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="start">The 0-based first line.</param>
    /// <param name="declarations">The declarations, by 0-based line.</param>
    /// <returns>The 0-based last line, inclusive.</returns>
    private static int Window(
        List<string> lines,
        int start,
        Dictionary<int, ExtractedSymbol> declarations)
    {
        var limit = Math.Min(lines.Count - 1, start + MaxLines - 1);
        var characters = 0;
        var last = start;
        var boundary = -1;

        for (var end = start; end <= limit; end++)
        {
            var length = lines[end].Length + 1;

            // A single line longer than the cap is still one line: splitting it would produce a citation that ends
            // mid-token, which is the one thing this class refuses to do.
            if (end > start && characters + length > MaxChars)
            {
                break;
            }

            characters += length;
            last = end;

            // A declaration that starts the next line ends this passage. This — not the 40-line cap — is what makes a
            // citation point at one declaration; without it a small class collapses into a single passage named after
            // its first member, and the exact-symbol boost that ranking depends on can never fire.
            if (end > start && declarations.ContainsKey(end + 1))
            {
                return end;
            }

            if (IsBoundary(lines, end + 1, declarations))
            {
                boundary = end;
            }
        }

        last = boundary >= start ? boundary : last;

        // Trailing blank lines belong to the last passage rather than to a passage of their own, as long as the cap
        // still holds; a run of blank lines long enough to overflow becomes its own (harmless) passage.
        while (last + 1 < lines.Count
            && last - start + 1 < MaxLines
            && string.IsNullOrWhiteSpace(lines[last + 1]))
        {
            last++;
        }

        return last;
    }

    /// <summary>
    /// Answers whether a passage may start at <paramref name="index"/>.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="index">The 0-based line a new passage would start at.</param>
    /// <param name="declarations">The declarations, by 0-based line.</param>
    /// <returns><see langword="true"/> at the start of a declaration, after a blank line, or at the end.</returns>
    private static bool IsBoundary(
        List<string> lines,
        int index,
        Dictionary<int, ExtractedSymbol> declarations)
    {
        if (index >= lines.Count)
        {
            return true;
        }

        if (declarations.ContainsKey(index))
        {
            return true;
        }

        return index > 0 && string.IsNullOrWhiteSpace(lines[index - 1]);
    }

    private static Dictionary<int, ExtractedSymbol> DeclarationsByLine(IReadOnlyList<ExtractedSymbol> symbols)
    {
        var declarations = new Dictionary<int, ExtractedSymbol>(symbols.Count);

        foreach (var symbol in symbols)
        {
            // Two declarations on one line ("public int X { get; } public int Y { get; }") can only be split once;
            // the first is the one the passage is named for.
            declarations.TryAdd(symbol.StartLine - 1, symbol);
        }

        return declarations;
    }

    /// <summary>
    /// Finds the declaration a passage is named after.
    /// </summary>
    /// <param name="declarations">The declarations, by 0-based line.</param>
    /// <param name="start">The passage's 0-based first line.</param>
    /// <param name="end">The passage's 0-based last line.</param>
    /// <returns>The declaration at the passage's first line, or the one its header block opens.</returns>
    private static ExtractedSymbol? DeclarationsAt(
        Dictionary<int, ExtractedSymbol> declarations,
        int start,
        int end)
    {
        // A passage often begins with the lines that introduce the declaration it holds — a namespace, a few
        // usings, the type header — and naming it after nothing would lose the exact-symbol boost for the very
        // passage that declares the symbol.
        var limit = Math.Min(end, start + LeadingHeaderLines);

        // The deepest declaration in the window wins: a passage that opens a type and then holds one whole member is
        // about that member, and the exact-symbol boost should land on it rather than on the type around it.
        for (var line = limit; line >= start; line--)
        {
            if (declarations.TryGetValue(line, out var symbol))
            {
                return symbol;
            }
        }

        return null;
    }
}

