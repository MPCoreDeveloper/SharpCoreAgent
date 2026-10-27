using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Extraction;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// Answers "who calls this, and what does it call?" by walking the stored call edges once.
/// </summary>
/// <remarks>
/// Only <c>calls</c> edges are followed. That is not an oversight: the tool's contract labels every block
/// <c>calls</c> or <c>called_by</c>, and both words describe a call. Inheritance and imports are reachable through
/// <c>get_evidence</c>'s neighbours instead, where a relation name is allowed to be anything the graph stores.
/// </remarks>
internal static class ReferenceRetriever
{
    /// <summary>The hard cap on blocks one reference query returns.</summary>
    internal const int MaxResults = 100;

    /// <summary>The relation label for a block that calls the subject.</summary>
    internal const string Calls = "calls";

    /// <summary>The relation label for a block the subject calls.</summary>
    internal const string CalledBy = "called_by";

    /// <summary>
    /// Finds the blocks on one side of a declaration's call edges.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="symbol">A short or qualified symbol name.</param>
    /// <param name="direction">Which side to follow.</param>
    /// <param name="maxResults">The requested cap; it is lowered to <see cref="MaxResults"/> when larger.</param>
    /// <returns>
    /// Blocks labelled with the relation that reached them, strongest edge first, then by path and line.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxResults"/> is not positive.</exception>
    public static IReadOnlyList<CodeEvidence> Find(
        IndexSnapshot snapshot,
        string symbol,
        ReferenceDirection direction,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        var subject = SymbolRetriever.Resolve(snapshot, symbol);
        if (subject is null)
        {
            return [];
        }

        var limit = Math.Min(maxResults, MaxResults);
        var strongest = new Dictionary<long, (SymbolRow Row, string Relation, double Weight)>();

        foreach (var edge in snapshot.Edges)
        {
            if (!string.Equals(edge.Relation, RelationKinds.Calls, StringComparison.Ordinal))
            {
                continue;
            }

            if (direction is ReferenceDirection.Callers or ReferenceDirection.Both &&
                edge.Target == subject.Id &&
                snapshot.Symbols.TryGetValue(edge.Source, out var caller))
            {
                Offer(strongest, caller, Calls, edge.Weight);
            }

            if (direction is ReferenceDirection.Callees or ReferenceDirection.Both &&
                edge.Source == subject.Id &&
                snapshot.Symbols.TryGetValue(edge.Target, out var callee))
            {
                Offer(strongest, callee, CalledBy, edge.Weight);
            }
        }

        var blocks = new List<CodeEvidence>(strongest.Count);

        foreach (var entry in strongest.Values)
        {
            if (EvidenceBlocks.ForSymbol(snapshot, entry.Row, entry.Relation, entry.Weight) is { } block)
            {
                blocks.Add(block);
            }
        }

        blocks.Sort(static (left, right) =>
        {
            var score = right.Score.CompareTo(left.Score);
            if (score != 0)
            {
                return score;
            }

            var path = string.CompareOrdinal(left.Path, right.Path);
            return path != 0 ? path : left.StartLine.CompareTo(right.StartLine);
        });

        return blocks.Count <= limit ? blocks : [.. blocks.Take(limit)];
    }

    /// <summary>
    /// Keeps the strongest edge when a symbol is reached twice with different labels.
    /// </summary>
    private static void Offer(
        Dictionary<long, (SymbolRow Row, string Relation, double Weight)> found,
        SymbolRow row,
        string relation,
        double weight)
    {
        if (found.TryGetValue(row.Id, out var existing) &&
            (existing.Weight > weight ||
             (existing.Weight.CompareTo(weight) == 0 && string.CompareOrdinal(existing.Relation, relation) <= 0)))
        {
            return;
        }

        found[row.Id] = (row, relation, weight);
    }
}
