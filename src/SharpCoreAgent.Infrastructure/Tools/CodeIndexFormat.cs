using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The shared vocabulary of the five retrieval answers: how a count, a size, a line and a block heading are spelled.
/// </summary>
/// <remarks>
/// One retrieval answer is a heading, a list and a few quoted lines, and all five tools spell those the same way. The
/// formatting lives here rather than in each tool because the contract fixes the words — <c>1 908</c>, <c>14.2 MiB</c>,
/// <c>why: lexical,vector</c>, <c>41│</c> — and a test can then assert one function instead of five copies.
/// <para>
/// Every number is formatted with the invariant culture: a transcript recorded on a machine with a comma decimal
/// separator must read the same as one recorded anywhere else, and a model parsing <c>w 0.83</c> must not see
/// <c>w 0,83</c>. Digit groups use a space rather than a comma, because a comma already separates list items.
/// </para>
/// </remarks>
internal static class CodeIndexFormat
{
    /// <summary>Gets the character budget one retrieval answer is shortened to.</summary>
    internal const int MaxOutputChars = 24_000;

    /// <summary>Gets how many lines of one passage a search answer quotes before it elides the rest.</summary>
    internal const int SearchExcerptLines = 40;

    /// <summary>Gets how many lines a reference block quotes.</summary>
    internal const int ReferenceExcerptLines = 12;

    /// <summary>Gets how many lines the passage at the centre of a context quotes.</summary>
    internal const int FocusExcerptLines = 20;

    private const int GutterWidth = 3;

    /// <summary>
    /// Renders a count the way the contract spells it: groups of three separated by a space.
    /// </summary>
    /// <param name="value">The count.</param>
    /// <returns>The digits, grouped, for example <c>1 908</c>.</returns>
    internal static string Number(long value)
    {
        var digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(digits.Length + (digits.Length / 3));

        for (var index = 0; index < digits.Length; index++)
        {
            if (index > 0 && (digits.Length - index) % 3 == 0)
            {
                builder.Append(' ');
            }

            builder.Append(digits[index]);
        }

        return value < 0 ? string.Concat("-", builder) : builder.ToString();
    }

    /// <summary>
    /// Renders a freshness as the lowercase word the tool output uses.
    /// </summary>
    /// <param name="freshness">The state to render.</param>
    /// <returns>The lowercase name, for example <c>fresh</c>.</returns>
    internal static string Freshness(IndexFreshness freshness) => freshness switch
    {
        IndexFreshness.Missing => "missing",
        IndexFreshness.Building => "building",
        IndexFreshness.Partial => "partial",
        IndexFreshness.Stale => "stale",
        IndexFreshness.Fresh => "fresh",
        IndexFreshness.Corrupt => "corrupt",
        _ => "unknown",
    };

    /// <summary>
    /// Renders the retrieval legs an index can offer, as a <c>+</c>-joined list.
    /// </summary>
    /// <param name="status">The index being read.</param>
    /// <returns><c>lexical+vector+graph</c>, or <c>lexical+graph</c> when vectors are unavailable.</returns>
    internal static string Legs(IndexStatus status)
    {
        var legs = "lexical";

        if (status.VectorSearchEnabled && !status.LexicalFallback)
        {
            legs += "+vector";
        }

        return legs + "+graph";
    }

    /// <summary>
    /// Renders the legs that produced one block, comma-separated, in <see cref="EvidenceLeg"/> order.
    /// </summary>
    /// <param name="legs">The legs recorded on the block.</param>
    /// <returns>For example <c>lexical,vector</c>; <c>unknown</c> when nothing was recorded.</returns>
    internal static string Legs(IReadOnlyList<EvidenceLeg> legs) =>
        legs.Count == 0 ? "unknown" : string.Join(',', legs.Select(leg => leg switch
        {
            EvidenceLeg.Lexical => "lexical",
            EvidenceLeg.Vector => "vector",
            EvidenceLeg.Graph => "graph",
            EvidenceLeg.Symbol => "symbol",
            EvidenceLeg.Path => "path",
            _ => "unknown",
        }));

    /// <summary>
    /// Renders a byte count with one decimal place.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>For example <c>14.2 MiB</c>.</returns>
    internal static string Size(long bytes) => bytes switch
    {
        < 0 => "0 B",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.0} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.0} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):0.0} GiB"),
    };

    /// <summary>
    /// Renders a duration in seconds, which is the unit a build report is read in.
    /// </summary>
    /// <param name="milliseconds">The elapsed time.</param>
    /// <returns>For example <c>0.4 s</c>.</returns>
    internal static string Seconds(long milliseconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1000d:0.0} s");

    /// <summary>
    /// Quotes a passage with its real line numbers in the margin.
    /// </summary>
    /// <param name="block">The block to quote.</param>
    /// <param name="maxLines">How many lines to quote before eliding the rest.</param>
    /// <returns>One line per source line, each prefixed with its number and <c>│</c>.</returns>
    internal static string Excerpt(CodeEvidence block, int maxLines) => Excerpt(block.Text, block.StartLine, maxLines);

    /// <summary>
    /// Quotes text with its real line numbers in the margin.
    /// </summary>
    /// <param name="text">The passage text, as it was indexed.</param>
    /// <param name="startLine">The 1-based line number of the first line.</param>
    /// <param name="maxLines">How many lines to quote before eliding the rest.</param>
    /// <returns>One line per source line, each prefixed with its number and <c>│</c>.</returns>
    /// <remarks>
    /// The elision is a line of its own (<c>… 12 more lines</c>) rather than a silent cut, so a reader can tell a
    /// short passage from a long one that was shortened. It is not <see cref="TextLimits.TruncatedMarker"/>: that
    /// marker means the whole answer hit its budget, which is a different fact.
    /// </remarks>
    internal static string Excerpt(string text, int startLine, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var shown = Math.Max(0, Math.Min(lines.Length, maxLines));
        var builder = new StringBuilder();

        for (var index = 0; index < shown; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(Line(startLine + index, lines[index]));
        }

        if (lines.Length > shown)
        {
            builder
                .Append('\n')
                .Append("  … ")
                .Append((lines.Length - shown).ToString(CultureInfo.InvariantCulture))
                .Append(" more lines");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Flattens a declaration line onto one line, because a tool answer is read line by line.
    /// </summary>
    /// <param name="signature">The declaration as written, or <see langword="null"/>.</param>
    /// <returns>The signature with runs of whitespace collapsed, or an empty string.</returns>
    internal static string Flatten(string? signature) =>
        string.IsNullOrWhiteSpace(signature)
            ? string.Empty
            : string.Join(' ', signature.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Renders one block's heading line.
    /// </summary>
    /// <param name="rank">The 1-based position of the block in the answer.</param>
    /// <param name="block">The block to describe.</param>
    /// <returns>
    /// <c>1) path:start-end · kind · symbol · sha256:… · why: legs</c>, with <c>stale</c> appended when the file
    /// changed after the build, and <c>unindexed</c> in place of the hash for a file the build never reached.
    /// </returns>
    internal static string BlockHeading(int rank, CodeEvidence block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var builder = new StringBuilder();

        builder
            .Append(rank.ToString(CultureInfo.InvariantCulture))
            .Append(") ")
            .Append(block.Path)
            .Append(':')
            .Append(block.StartLine.ToString(CultureInfo.InvariantCulture))
            .Append('-')
            .Append(block.EndLine.ToString(CultureInfo.InvariantCulture))
            .Append(" · ")
            .Append(block.Kind);

        if (!string.IsNullOrEmpty(block.Symbol))
        {
            builder.Append(" · ").Append(block.Symbol);
        }

        builder
            .Append(" · ")
            .Append(string.IsNullOrEmpty(block.Sha8) ? "unindexed" : string.Concat("sha256:", block.Sha8))
            .Append(" · why: ")
            .Append(Legs(block.Legs));

        if (block.IsStale)
        {
            builder.Append(" · stale");
        }

        return builder.ToString();
    }

    private static string Line(int number, string text) => string.Create(
        CultureInfo.InvariantCulture,
        $"  {number.ToString(CultureInfo.InvariantCulture).PadLeft(GutterWidth)}│{text.Replace("\t", "    ", StringComparison.Ordinal)}");
}
