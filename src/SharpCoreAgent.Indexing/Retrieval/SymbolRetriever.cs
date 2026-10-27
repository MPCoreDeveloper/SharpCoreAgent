using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Extraction;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// Answers "where is this declaration declared?" from the snapshot, in three exactness tiers.
/// </summary>
/// <remarks>
/// The tiers exist because the three questions a model asks with one name are different questions. An exact hit
/// (<c>AgentLoop.TurnAsync</c>) is the declaration it meant; a case-insensitive hit is the same name typed from
/// memory; a substring hit is a symbol the model half-remembers. Returning them sorted by tier, then by path and
/// line, is what makes a repeated query against the same build produce the same list in the same order.
/// </remarks>
internal static class SymbolRetriever
{
    /// <summary>The hard cap on declarations one query returns.</summary>
    internal const int MaxResults = 50;

    /// <summary>
    /// Finds declarations by name.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="name">A short name, a qualified name or a fragment of one.</param>
    /// <param name="kind">A declaration kind to restrict to, or <see langword="null"/> for all of them.</param>
    /// <param name="maxResults">The requested cap; it is lowered to <see cref="MaxResults"/> when larger.</param>
    /// <returns>The declarations, best tier first, then by path and line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxResults"/> is not positive.</exception>
    public static IReadOnlyList<CodeSymbolRef> Search(
        IndexSnapshot snapshot,
        string name,
        string? kind,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        var needle = name.Trim();
        var limit = Math.Min(maxResults, MaxResults);
        var matches = new List<(int Tier, SymbolRow Row)>();

        foreach (var row in snapshot.Symbols.Values)
        {
            if (kind is { Length: > 0 } && !string.Equals(row.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tier = TierOf(row, needle);
            if (tier >= 0)
            {
                matches.Add((tier, row));
            }
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

        var calls = CallCounts(snapshot);

        return [.. matches.Take(limit).Select(match => ToRef(match.Row, calls.GetValueOrDefault(match.Row.Id)))];
    }

    /// <summary>
    /// Builds the public reference for one stored declaration.
    /// </summary>
    /// <param name="snapshot">The build the declaration belongs to.</param>
    /// <param name="row">The declaration.</param>
    /// <returns>The reference a tool renders.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="row"/> is null.</exception>
    /// <remarks>
    /// The call counts come from one pass over the edges, so this overload is for a single declaration. A caller
    /// that needs several uses <see cref="InFile"/>, which pays for that pass once.
    /// </remarks>
    public static CodeSymbolRef ToRef(IndexSnapshot snapshot, SymbolRow row)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(row);

        return ToRef(row, CallCounts(snapshot).GetValueOrDefault(row.Id));
    }

    /// <summary>
    /// Lists the declarations of one file in line order.
    /// </summary>
    /// <param name="snapshot">The build being read.</param>
    /// <param name="fileId">The <c>files.id</c> to list.</param>
    /// <param name="limit">The largest number of declarations to return.</param>
    /// <returns>The declarations, ordered by line and then by qualified name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    /// <remarks>
    /// <c>code_context</c> answers a path target by naming what the file declares before it describes the
    /// neighbourhood, and this is that list. Ordering by line rather than by centrality is deliberate: the reader is
    /// looking at the file, so the answer should read like the file.
    /// </remarks>
    public static IReadOnlyList<CodeSymbolRef> InFile(IndexSnapshot snapshot, long fileId, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var calls = CallCounts(snapshot);

        var rows = snapshot.Symbols.Values
            .Where(row => row.FileId == fileId)
            .OrderBy(row => row.StartLine)
            .ThenBy(row => row.QualifiedName, StringComparer.Ordinal)
            .Take(limit);

        return [.. rows.Select(row => ToRef(row, calls.GetValueOrDefault(row.Id)))];
    }

    /// <summary>
    /// Maps a declaration and its call counts onto the public reference.
    /// </summary>
    /// <param name="row">The declaration.</param>
    /// <param name="calls">How many call sites point at it and how many it has.</param>
    /// <returns>The reference a tool renders.</returns>
    private static CodeSymbolRef ToRef(SymbolRow row, (int Callers, int Callees) calls) => new()
    {
        Key = row.Key,
        Name = row.Name,
        QualifiedName = row.QualifiedName,
        Kind = row.Kind,
        Path = row.Path,
        StartLine = row.StartLine,
        EndLine = row.EndLine,
        Signature = row.Signature,
        Callers = calls.Callers,
        Callees = calls.Callees,
        Community = row.Community,
    };


    /// <summary>
    /// Resolves one symbol name to the single declaration it most likely means.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="name">A short name, a qualified name or a fragment of one.</param>
    /// <returns>
    /// The best match — the earliest tier, and within a tier the most central declaration — or
    /// <see langword="null"/> when nothing matches.
    /// </returns>
    /// <remarks>
    /// <c>find_references</c> and <c>code_context</c> take one subject, so they need this narrowing: asking for the
    /// callers of <c>TurnAsync</c> is a question about one declaration, and answering it with the union of five
    /// overloads would be a different, larger answer than the model asked for.
    /// </remarks>
    public static SymbolRow? Resolve(IndexSnapshot snapshot, string name)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var needle = name.Trim();
        SymbolRow? best = null;
        var bestTier = int.MaxValue;

        foreach (var row in snapshot.Symbols.Values)
        {
            var tier = TierOf(row, needle);
            if (tier < 0)
            {
                continue;
            }

            if (tier < bestTier || (tier == bestTier && best is not null && Precedes(row, best)))
            {
                best = row;
                bestTier = tier;
            }
        }

        return best;

        // A more central declaration is the better answer at the same tier: it is the one the workspace itself
        // leans on. Path and line break the remaining ties so the choice is stable across builds.
        static bool Precedes(SymbolRow candidate, SymbolRow current)
        {
            if (candidate.Centrality.CompareTo(current.Centrality) != 0)
            {
                return candidate.Centrality > current.Centrality;
            }

            var path = string.CompareOrdinal(candidate.Path, current.Path);
            return path != 0 ? path < 0 : candidate.StartLine < current.StartLine;
        }
    }

    /// <summary>
    /// Reads each declaration's call-site counts from the stored edges.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <returns>Callers and callees per symbol id; a missing id means zero of each.</returns>
    internal static Dictionary<long, (int Callers, int Callees)> CallCounts(IndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var counts = new Dictionary<long, (int Callers, int Callees)>();

        foreach (var edge in snapshot.Edges)
        {
            if (!string.Equals(edge.Relation, RelationKinds.Calls, StringComparison.Ordinal))
            {
                continue;
            }

            var source = counts.GetValueOrDefault(edge.Source);
            counts[edge.Source] = (source.Callers, source.Callees + 1);

            var target = counts.GetValueOrDefault(edge.Target);
            counts[edge.Target] = (target.Callers + 1, target.Callees);
        }

        return counts;
    }

    /// <summary>
    /// Scores how exactly one declaration matches a name.
    /// </summary>
    /// <param name="row">The declaration.</param>
    /// <param name="needle">The trimmed name the caller asked for.</param>
    /// <returns>0 for an exact match, 1 for a case-insensitive one, 2 for a substring, and -1 for no match.</returns>
    private static int TierOf(SymbolRow row, string needle)
    {
        if (string.Equals(row.QualifiedName, needle, StringComparison.Ordinal) ||
            string.Equals(row.Key, needle, StringComparison.Ordinal) ||
            string.Equals(row.Name, needle, StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(row.QualifiedName, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Key, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Name, needle, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return row.QualifiedName.Contains(needle, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }
}
