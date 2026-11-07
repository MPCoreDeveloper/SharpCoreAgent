using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// One documentation section as retrieval reads it: where it is, its heading, the declaration it describes and its text.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="CodeDocRef"/> so the retriever never has to know which row it came from: the adapter
/// assembles a <see cref="DocRow"/> from the <c>docs</c> table and its neighbours, and the retriever turns rows into the
/// public contract without touching a database.
/// </remarks>
internal sealed record DocRow
{
    /// <summary>Gets the workspace-relative path of the declaring file.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the heading, without <c>#</c> markers, or <see langword="null"/>.</summary>
    public string? Heading { get; init; }

    /// <summary>Gets the qualified name of the declaration the heading names, or <see langword="null"/>.</summary>
    public string? Symbol { get; init; }

    /// <summary>Gets the 1-based first line, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line, inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the section text.</summary>
    public required string Text { get; init; }
}

/// <summary>
/// Answers "which documentation describes this?" over the sections the build stored.
/// </summary>
/// <remarks>
/// The match is deliberately literal: every whitespace-separated term of the query must appear, case-insensitively, in
/// the section's heading plus its text. There is no stemming and no ranking by word frequency — a documentation lookup
/// is a name or a phrase a reader already knows, and returning prose that merely shares a common word is the failure
/// mode to avoid. Ordering is a heading hit first, then path and line, which is stable across builds.
/// </remarks>
internal static class DocumentationRetriever
{
    /// <summary>The hard cap on sections one query returns, whatever the caller asked for.</summary>
    internal const int MaxResults = 50;

    /// <summary>
    /// Finds the sections that answer one phrase.
    /// </summary>
    /// <param name="docs">Every documentation section the build holds.</param>
    /// <param name="text">The phrase the caller wrote.</param>
    /// <param name="maxResults">The requested cap; it is lowered to <see cref="MaxResults"/> when larger.</param>
    /// <returns>The matching sections, heading hits first, then by path and line; empty when nothing matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="docs"/> or <paramref name="text"/> is null.</exception>
    public static IReadOnlyList<CodeDocRef> Search(IReadOnlyList<DocRow> docs, string text, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(text);

        if (maxResults <= 0)
        {
            return [];
        }

        var terms = Terms(text);
        if (terms.Length == 0)
        {
            return [];
        }

        var matches = new List<(int Tier, DocRow Row)>();

        foreach (var doc in docs)
        {
            if (!ContainsAll(doc, terms))
            {
                continue;
            }

            matches.Add((HeadingMatches(doc.Heading, terms) ? 0 : 1, doc));
        }

        if (matches.Count == 0)
        {
            return [];
        }

        matches.Sort(static (left, right) =>
        {
            var tier = left.Tier.CompareTo(right.Tier);
            if (tier != 0)
            {
                return tier;
            }

            var path = string.CompareOrdinal(left.Row.Path, right.Row.Path);
            return path != 0 ? path : left.Row.StartLine.CompareTo(right.Row.StartLine);
        });

        var limit = Math.Min(maxResults, MaxResults);

        return [.. matches.Take(limit).Select(match => new CodeDocRef
        {
            Path = match.Row.Path,
            Heading = match.Row.Heading,
            Symbol = match.Row.Symbol,
            StartLine = match.Row.StartLine,
            EndLine = match.Row.EndLine,
            Text = match.Row.Text,
        })];
    }

    /// <summary>
    /// Splits a query into the distinct lowercased terms a section must all contain.
    /// </summary>
    /// <param name="text">The caller's phrase.</param>
    /// <returns>The terms; empty for a blank query.</returns>
    private static string[] Terms(string text) =>
        [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Reports whether a section's heading and text contain every term.
    /// </summary>
    /// <param name="doc">The section.</param>
    /// <param name="terms">The query terms, lowercased.</param>
    /// <returns><see langword="true"/> when every term appears in the heading or the text.</returns>
    private static bool ContainsAll(DocRow doc, string[] terms)
    {
        var haystack = doc.Heading is { Length: > 0 } heading ? heading + "\n" + doc.Text : doc.Text;

        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reports whether a heading contains every term, which is what ranks a section above one merely quoting them.
    /// </summary>
    /// <param name="heading">The section heading, or <see langword="null"/>.</param>
    /// <param name="terms">The query terms, lowercased.</param>
    /// <returns><see langword="true"/> when the heading holds every term.</returns>
    private static bool HeadingMatches(string? heading, string[] terms) =>
        !string.IsNullOrEmpty(heading)
        && terms.All(term => heading.Contains(term, StringComparison.OrdinalIgnoreCase));
}
