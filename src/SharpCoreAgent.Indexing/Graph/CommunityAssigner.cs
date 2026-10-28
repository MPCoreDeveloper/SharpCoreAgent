using System.Globalization;
using SharpCoreDB.Graph.Advanced;
using SharpCoreDB.Graph.Advanced.CommunityDetection;
using SharpCoreDB.Graph.Advanced.Metrics;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing.Graph;

/// <summary>
/// What one community pass found and wrote.
/// </summary>
/// <remarks>
/// The counters exist for the build path and for tests: the columns are the product, and a caller that wants the
/// numbers a build reports reads them back from <c>symbols</c> (which is what <c>IndexStatus</c> does), so nothing
/// downstream depends on this record surviving the call.
/// </remarks>
internal sealed record CommunityAssignment
{
    /// <summary>Gets the number of distinct labels written.</summary>
    public required int Communities { get; init; }

    /// <summary>Gets how many declarations the graph connects to at least one other declaration.</summary>
    public required int Members { get; init; }

    /// <summary>Gets how many declarations have no relation at all, and therefore no cluster.</summary>
    public required int Isolated { get; init; }

    /// <summary>Gets the assignment of an index that holds no declarations.</summary>
    public static CommunityAssignment None { get; } = new() { Communities = 0, Members = 0, Isolated = 0 };
}

/// <summary>
/// Assigns every declaration to a Louvain community and a degree centrality, once per build.
/// </summary>
/// <remarks>
/// <para>
/// Communities are a whole-graph answer, so they are computed where the whole graph exists — at the end of a build,
/// after every edge has been resolved — and stored as two columns. A query then reads a label instead of running
/// Louvain, which is the trade <c>research.md</c> D10 records: precompute the expensive part, keep the cheap part
/// (the traversal) for the turn that needs it.
/// </para>
/// <para>
/// Three rules make the stored labels reproducible, because a label that moves between builds would make two
/// identical indexes look different to a reader:
/// </para>
/// <list type="number">
/// <item>Nodes enter the graph in <c>key</c> order, so the algorithm sees the same input for the same corpus
/// whatever order the files were written in.</item>
/// <item>An edge is treated as undirected and unweighted: a call from A to B puts A and B in one cluster, and the
/// relation weights stay a traversal signal rather than becoming a clustering one.</item>
/// <item>Communities are numbered <c>c0</c>, <c>c1</c>, … by the ordinal position of each community's
/// ordinally-first member, not by the order the algorithm happened to emit them.</item>
/// </list>
/// <para>
/// A declaration with no edge at all belongs to no cluster, and storing it as a singleton of its own would say
/// otherwise: the highest-degree declaration of a one-member community is not the centre of anything. Those rows get
/// an empty label, which the reader maps to <see langword="null"/> — the value every consumer already spells "not
/// known" with.
/// </para>
/// </remarks>
internal static class CommunityAssigner
{
    /// <summary>The prefix of a stored label; the number is the community's rank in this build.</summary>
    private const string LabelPrefix = "c";

    private const string SelectSymbols = "SELECT id, key FROM symbols";
    private const string SelectEdges = "SELECT source, target FROM edges";
    private const string UpdateSymbol = "UPDATE symbols SET community = @community, centrality = @centrality WHERE id = @id";

    /// <summary>
    /// Clusters the stored graph and writes <c>symbols.community</c> and <c>symbols.centrality</c>.
    /// </summary>
    /// <param name="database">The index the build has just finished writing.</param>
    /// <param name="cancellationToken">Cancels the pass; the columns then keep their previous values.</param>
    /// <returns>The counters of the pass.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    /// <remarks>
    /// Every row is rewritten, including the isolated ones, because a previous build may have clustered a
    /// declaration whose last caller has since been deleted: leaving that label behind would answer with a
    /// community that no longer exists.
    /// </remarks>
    public static async ValueTask<CommunityAssignment> AssignAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        var symbols = ReadSymbols(database);
        if (symbols.Count == 0)
        {
            return CommunityAssignment.None;
        }

        var positions = new Dictionary<long, int>(symbols.Count);
        for (var index = 0; index < symbols.Count; index++)
        {
            positions.TryAdd(symbols[index].Id, index);
        }

        var adjacency = ReadAdjacency(database, positions);
        var connected = Enumerable.Range(0, symbols.Count).Where(index => adjacency[index].Length > 0).ToArray();

        var labels = new string[symbols.Count];
        var centrality = new double[symbols.Count];
        var communities = 0;

        // A single connected declaration is not a cluster, and Louvain addresses nodes by array index, so the graph
        // is only handed over when there is something to partition.
        if (connected.Length > 1)
        {
            communities = await PartitionAsync(
                    symbols,
                    adjacency,
                    connected,
                    positions,
                    labels,
                    centrality,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Write(database, symbols, labels, centrality);

        return new CommunityAssignment
        {
            Communities = communities,
            Members = connected.Length,
            Isolated = symbols.Count - connected.Length,
        };
    }

    /// <summary>
    /// Runs Louvain and degree centrality over the connected part of the graph and records both per declaration.
    /// </summary>
    /// <param name="symbols">The declarations, in <c>key</c> order.</param>
    /// <param name="adjacency">Each declaration's neighbours, as positions in <paramref name="symbols"/>.</param>
    /// <param name="connected">The positions that have at least one neighbour, ascending.</param>
    /// <param name="positions">The position of each declaration id.</param>
    /// <param name="labels">Filled with the label of each position.</param>
    /// <param name="centrality">Filled with the normalized degree of each position.</param>
    /// <param name="cancellationToken">Cancels the algorithms.</param>
    /// <returns>The number of communities.</returns>
    private static async ValueTask<int> PartitionAsync(
        IReadOnlyList<SymbolNode> symbols,
        int[][] adjacency,
        int[] connected,
        Dictionary<long, int> positions,
        string[] labels,
        double[] centrality,
        CancellationToken cancellationToken)
    {
        // Louvain addresses a node by its index, so the declarations are renumbered densely: only the connected
        // ones enter, in the same key order they arrived in.
        var nodeOf = new int[symbols.Count];
        Array.Fill(nodeOf, -1);

        var ids = new ulong[connected.Length];

        for (var node = 0; node < connected.Length; node++)
        {
            var position = connected[node];
            nodeOf[position] = node;
            ids[node] = (ulong)symbols[position].Id;
        }

        // A second pass, because a neighbour's position may be later than this node's: mapping while assigning
        // would hand the algorithm -1 as a node index.
        var neighbours = new int[connected.Length][];
        for (var node = 0; node < connected.Length; node++)
        {
            neighbours[node] = [.. adjacency[connected[node]].Select(neighbour => nodeOf[neighbour])];
        }

        var graph = new GraphData { NodeIds = ids, AdjacencyList = neighbours };

        var detection = await new LouvainAlgorithm()
            .ExecuteAsync(graph, cancellationToken)
            .ConfigureAwait(false);
        var degrees = await new DegreeCentrality()
            .CalculateAsync(graph, cancellationToken)
            .ConfigureAwait(false);

        // Numbering by the ordinally-first member is what makes the labels stable: the algorithm's own community
        // ids depend on the order it merged in, which is an implementation detail rather than a fact about the
        // corpus.
        var numbered = detection.Communities
            .Select(community => community.Members.Select(id => positions[(long)id]).ToArray())
            .OrderBy(members => members.Min())
            .ToArray();

        for (var rank = 0; rank < numbered.Length; rank++)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"{LabelPrefix}{rank}");
            foreach (var position in numbered[rank])
            {
                labels[position] = label;
            }
        }

        foreach (var metric in degrees)
        {
            if (positions.TryGetValue((long)metric.NodeId, out var position))
            {
                centrality[position] = metric.Value;
            }
        }

        return numbered.Length;
    }

    /// <summary>
    /// Reads the declarations in <c>key</c> order, which is the order the algorithms see them in.
    /// </summary>
    /// <param name="database">The index to read.</param>
    /// <returns>The declarations; a row without an id or a key is dropped, because it cannot be addressed.</returns>
    private static List<SymbolNode> ReadSymbols(IDatabase database) =>
    [
        .. IndexSql.Query(database, SelectSymbols)
            .Select(row => new SymbolNode(
                IndexSql.AsLong(row.GetValueOrDefault("id")),
                IndexSql.AsText(row.GetValueOrDefault("key"))))
            .Where(symbol => symbol.Id > 0 && symbol.Key.Length > 0)
            .OrderBy(symbol => symbol.Key, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Id),
    ];

    /// <summary>
    /// Reads the edge table as an undirected adjacency list over declaration positions.
    /// </summary>
    /// <param name="database">The index to read.</param>
    /// <param name="positions">The position of each declaration id.</param>
    /// <returns>One sorted, duplicate-free neighbour array per declaration.</returns>
    /// <remarks>
    /// A relation with an endpoint this build cannot name is skipped rather than counted, which keeps the pass
    /// honest about a half-resolved graph: it clusters what it can address and leaves the rest unlabelled.
    /// </remarks>
    private static int[][] ReadAdjacency(IDatabase database, Dictionary<long, int> positions)
    {
        var adjacency = new HashSet<int>[positions.Count];
        for (var index = 0; index < adjacency.Length; index++)
        {
            adjacency[index] = [];
        }

        foreach (var row in IndexSql.Query(database, SelectEdges))
        {
            var source = IndexSql.AsLong(row.GetValueOrDefault("source"));
            var target = IndexSql.AsLong(row.GetValueOrDefault("target"));

            if (source == target
                || !positions.TryGetValue(source, out var from)
                || !positions.TryGetValue(target, out var to))
            {
                continue;
            }

            adjacency[from].Add(to);
            adjacency[to].Add(from);
        }

        return [.. adjacency.Select(neighbours => neighbours.Order().ToArray())];
    }

    /// <summary>
    /// Writes both columns for every declaration in one batch.
    /// </summary>
    /// <param name="database">The index to write.</param>
    /// <param name="symbols">The declarations, in the order the arrays are indexed by.</param>
    /// <param name="labels">The label of each declaration; empty for one with no cluster.</param>
    /// <param name="centrality">The normalized degree of each declaration.</param>
    /// <remarks>
    /// One batch, so an interrupted pass leaves the previous labels rather than half of the new ones.
    /// </remarks>
    private static void Write(
        IDatabase database,
        IReadOnlyList<SymbolNode> symbols,
        string[] labels,
        double[] centrality)
    {
        database.BeginBatchUpdate();
        try
        {
            for (var index = 0; index < symbols.Count; index++)
            {
                IndexSql.Execute(
                    database,
                    UpdateSymbol,
                    IndexSql.Parameters(
                        ("@id", symbols[index].Id),
                        ("@community", labels[index]),
                        ("@centrality", centrality[index])));
            }

            database.EndBatchUpdate();
        }
        catch (Exception)
        {
            database.CancelBatchUpdate();
            throw;
        }
    }

    /// <summary>One declaration as the pass needs it: the row it is written back to, and its ordering key.</summary>
    /// <param name="Id">The <c>symbols.id</c>.</param>
    /// <param name="Key">The unique key the declarations are ordered by.</param>
    private readonly record struct SymbolNode(long Id, string Key);
}
