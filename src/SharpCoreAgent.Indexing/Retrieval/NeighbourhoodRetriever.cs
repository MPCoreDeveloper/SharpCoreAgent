using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Extraction;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// One declaration reached from a seed, with the hop that reached it.
/// </summary>
/// <param name="Row">The declaration.</param>
/// <param name="Relation">How it relates to the declaration one hop closer to the seed.</param>
/// <param name="Weight">The weakest edge along the path, normalised to 0–1.</param>
/// <param name="Hops">How many edges separate it from the seed.</param>
internal readonly record struct NeighbourNode(SymbolRow Row, string Relation, double Weight, int Hops);

/// <summary>
/// The declarations one walk reached, and how many it found before the caller's cap.
/// </summary>
/// <remarks>
/// The count is kept beside the list because the two answer different questions: the list is the answer, and the
/// count is the reason the answer is short (<c>8 shown of 26</c>). Reporting only the list would let a caller
/// believe a neighbourhood ended where the cap fell.
/// </remarks>
/// <param name="Nodes">The neighbours, strongest first.</param>
/// <param name="Considered">How many neighbours the walk found before <c>max</c> was applied.</param>
internal sealed record Neighbourhood(IReadOnlyList<NeighbourNode> Nodes, int Considered);

/// <summary>
/// Answers "what sits around this file or declaration?" by broadening through the symbol graph.
/// </summary>
/// <remarks>
/// The walk is a breadth-first search that keeps the strongest path to each declaration, and the strength of a path
/// is its weakest edge. A caller that mentions a symbol once is a lead; a caller that calls it five times is its
/// neighbourhood, and the minimum is what tells those two cases apart without inventing a formula nobody can check.
/// Declarations weaker than <see cref="MinWeight"/> are dropped rather than ranked last: a graph answer that lists
/// everything the workspace contains is not an answer to "what is near this?".
/// </remarks>
internal static class NeighbourhoodRetriever
{
    /// <summary>The largest number of hops a caller may ask for.</summary>
    internal const int MaxHops = 3;

    /// <summary>The largest number of neighbours a caller may ask for.</summary>
    internal const int MaxNeighbours = 12;

    /// <summary>The weakest normalised path a neighbour may have.</summary>
    internal const double MinWeight = 0.2;

    /// <summary>The edge weight the stored maximum (<c>5</c>) normalises against.</summary>
    private const double MaxEdgeWeight = 5d;

    /// <summary>
    /// Finds the file a caller named, when the target is a path the index holds.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="target">A workspace-relative path.</param>
    /// <returns>The stored file, or <see langword="null"/> when no indexed file has that path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is blank.</exception>
    public static FileRow? ResolveFile(IndexSnapshot snapshot, string target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var trimmed = Normalize(target);

        return snapshot.Files.Values.FirstOrDefault(
            file => string.Equals(file.Path, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the file path or symbol name a caller used to name the centre of the neighbourhood.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="target">A workspace-relative path, or a symbol name.</param>
    /// <returns>
    /// The declaration to start from: the most central declaration in the named file, or the declaration the name
    /// matches best; <see langword="null"/> when neither interpretation finds anything.
    /// </returns>
    public static SymbolRow? ResolveSeed(IndexSnapshot snapshot, string target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var trimmed = Normalize(target);

        if (ResolveFile(snapshot, trimmed) is { } file)
        {
            SymbolRow? inFile = null;

            foreach (var row in snapshot.Symbols.Values)
            {
                if (row.FileId == file.Id &&
                    (inFile is null || row.Centrality > inFile.Centrality ||
                     (row.Centrality.CompareTo(inFile.Centrality) == 0 && row.StartLine < inFile.StartLine)))
                {
                    inFile = row;
                }
            }

            if (inFile is not null)
            {
                return inFile;
            }
        }

        return SymbolRetriever.Resolve(snapshot, trimmed);
    }

    /// <summary>
    /// Renders a caller-supplied target the way the stored paths are written.
    /// </summary>
    /// <param name="target">A path or a symbol name.</param>
    /// <returns>The trimmed value with forward slashes and no leading <c>./</c>.</returns>
    private static string Normalize(string target)
    {
        var trimmed = target.Trim().Replace('\\', '/');

        return trimmed.StartsWith("./", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }


    /// <summary>
    /// Broadens from one declaration through the stored edges.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="seed">The declaration to start from.</param>
    /// <param name="hops">How many edges to follow; it is clamped to <see cref="MaxHops"/>.</param>
    /// <param name="max">The requested cap; it is lowered to <see cref="MaxNeighbours"/> when larger.</param>
    /// <returns>
    /// The neighbours, strongest first, then by path and line, together with how many the walk found before the cap.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="seed"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hops"/> or <paramref name="max"/> is not positive.</exception>
    public static Neighbourhood Expand(
        IndexSnapshot snapshot,
        SymbolRow seed,
        int hops,
        int max)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hops);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        var depth = Math.Min(hops, MaxHops);
        var limit = Math.Min(max, MaxNeighbours);
        var adjacency = BuildAdjacency(snapshot);
        var reached = new Dictionary<long, NeighbourNode>();
        var frontier = new List<(long Id, double Weight)> { (seed.Id, 1d) };

        for (var hop = 1; hop <= depth && frontier.Count > 0; hop++)
        {
            var next = new List<(long Id, double Weight)>();

            foreach (var (id, weight) in frontier)
            {
                if (!adjacency.TryGetValue(id, out var edges))
                {
                    continue;
                }

                foreach (var (targetId, relation, edgeWeight) in edges)
                {
                    if (targetId == seed.Id || !snapshot.Symbols.TryGetValue(targetId, out var row))
                    {
                        continue;
                    }

                    var pathWeight = Math.Min(weight, Normalise(edgeWeight));
                    if (pathWeight < MinWeight)
                    {
                        continue;
                    }

                    if (reached.TryGetValue(targetId, out var existing) && existing.Weight >= pathWeight)
                    {
                        continue;
                    }

                    reached[targetId] = new NeighbourNode(row, relation, pathWeight, hop);
                    next.Add((targetId, pathWeight));
                }
            }

            frontier = next;
        }

        var nodes = new List<NeighbourNode>(reached.Values);
        nodes.Sort(static (left, right) =>
        {
            var weight = right.Weight.CompareTo(left.Weight);
            if (weight != 0)
            {
                return weight;
            }

            var hops = left.Hops.CompareTo(right.Hops);
            if (hops != 0)
            {
                return hops;
            }

            var path = string.CompareOrdinal(left.Row.Path, right.Row.Path);
            return path != 0 ? path : left.Row.StartLine.CompareTo(right.Row.StartLine);
        });

        return new Neighbourhood(nodes.Count <= limit ? nodes : [.. nodes.Take(limit)], nodes.Count);
    }

    /// <summary>
    /// Builds the bidirectional adjacency view of the stored edges.
    /// </summary>
    private static Dictionary<long, List<(long Target, string Relation, double Weight)>> BuildAdjacency(
        IndexSnapshot snapshot)
    {
        var adjacency = new Dictionary<long, List<(long Target, string Relation, double Weight)>>();

        foreach (var edge in snapshot.Edges)
        {
            Link(adjacency, edge.Source, edge.Target, edge.Relation, edge.Weight);
            Link(adjacency, edge.Target, edge.Source, Reverse(edge.Relation), edge.Weight);
        }

        return adjacency;

        static void Link(
            Dictionary<long, List<(long Target, string Relation, double Weight)>> adjacency,
            long source,
            long target,
            string relation,
            double weight)
        {
            if (!adjacency.TryGetValue(source, out var edges))
            {
                edges = [];
                adjacency[source] = edges;
            }

            edges.Add((target, relation, weight));
        }
    }

    /// <summary>
    /// Reads an edge backwards.
    /// </summary>
    private static string Reverse(string relation) => relation switch
    {
        RelationKinds.Calls => ReferenceRetriever.CalledBy,
        ReferenceRetriever.CalledBy => RelationKinds.Calls,
        RelationKinds.References => "referenced_by",
        RelationKinds.Inherits => "inherited_by",
        RelationKinds.Implements => "implemented_by",
        RelationKinds.Imports => "imported_by",
        _ => relation,
    };

    /// <summary>
    /// Converts a stored edge weight into the 0–1 scale a path strength uses.
    /// </summary>
    private static double Normalise(double weight) =>
        double.IsFinite(weight) ? Math.Clamp(weight / MaxEdgeWeight, 0d, 1d) : 0d;
}
