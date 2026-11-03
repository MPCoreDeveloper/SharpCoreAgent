namespace SharpCoreAgent.Indexing.Graph;

/// <summary>
/// The partition the <c>louvain</c> step runs: modularity local moving, then the communities it found become the next
/// level's nodes, until a level merges nothing.
/// </summary>
/// <remarks>
/// <para>
/// The objective is written here because this type is the only definition of it the index has:
/// <c>Q = Σ_c [l_c / m − (d_c / 2m)²]</c> — internal weight over total weight, minus the square of the community's
/// share of all degrees. A node moves to a neighbouring community only when that number rises, and a level's answer is
/// kept only when coarsening it merges something.
/// </para>
/// <para>
/// What this replaces is worth recording. The store's <c>LouvainAlgorithm</c> answers the same question by recomputing
/// the whole partition's modularity — once per candidate move, and it considers every neighbour community of every node
/// — which is <c>O(n + m)</c> per candidate and therefore quadratic in the node count. At 4 123 nodes and 10 744 edges
/// that was 71 s; the same objective, computed from the weights a move already has to sum, is <c>O(degree)</c> per
/// candidate and 0.15 s. It is also correct where that one was not: it counted each internal edge from both endpoints,
/// so what it maximised was not this formula, and its own reported value (1.001678) exceeded the 1 that modularity
/// cannot pass.
/// </para>
/// <para>
/// Determinism is by construction rather than by observation: nodes are swept in the order they arrived, candidate
/// communities are visited in ascending id rather than in dictionary order, an exact tie keeps the community the node
/// is in, and the level loop ends on the graph's own structure — no clock, no hash code, no random start. The same
/// adjacency list therefore produces the same labels in this process and the next one, which is what makes two builds of
/// one corpus comparable.
/// </para>
/// </remarks>
internal static class ModularityPartition
{
    /// <summary>How many times a level may be coarsened; five is this index's depth, so this is a guard.</summary>
    private const int MaxLevels = 8;

    /// <summary>How many sweeps a level may take; a converging sweep moves nobody, so this is a guard too.</summary>
    private const int MaxSweeps = 64;

    /// <summary>
    /// How much better a move must be before it is taken. Weights are exact integers at the first level and sums of
    /// them above it, so the only place this matters is a tie that floating point made unequal in the last bit.
    /// </summary>
    private const double Tolerance = 1e-12;

    /// <summary>
    /// Partitions an undirected graph.
    /// </summary>
    /// <param name="adjacency">Each node's neighbours, by node index. Edges are unweighted and undirected.</param>
    /// <param name="cancellationToken">Cancels between levels and between sweeps.</param>
    /// <returns>The community of each node, as an index into the returned array's own values — not a label.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adjacency"/> is null.</exception>
    /// <remarks>
    /// The returned numbers are dense (<c>0..k−1</c>) and are the caller's to render: this method knows nothing about
    /// the <c>c0</c>, <c>c1</c>, … numbering the pass stores, because that numbering is a rule about declarations in
    /// <c>key</c> order rather than a fact about the graph.
    /// </remarks>
    public static int[] Nodes(int[][] adjacency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjacency);

        if (adjacency.Length == 0)
        {
            return [];
        }

        var nodes = adjacency;
        var weights = new double[nodes.Length][];
        for (var node = 0; node < nodes.Length; node++)
        {
            weights[node] = [.. nodes[node].Select(static _ => 1.0)];
        }

        var loops = new double[nodes.Length];
        var membership = Enumerable.Range(0, nodes.Length).ToArray();
        var level = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var partition = Sweep(nodes, weights, loops, cancellationToken);
            var map = Dense(partition, out var merged);

            // Compose the level's answer into the mapping back to the original nodes: a node's community here is a
            // node of the coarsened graph, and the next level numbers those from zero.
            for (var node = 0; node < membership.Length; node++)
            {
                membership[node] = map[partition[membership[node]]];
            }

            level++;

            // A level that merged nothing is the answer; the cap is there for a graph that never settles.
            if (merged >= nodes.Length || level >= MaxLevels)
            {
                break;
            }

            (nodes, weights, loops) = Coarsen(nodes, weights, loops, partition, map, merged);
        }

        return membership;
    }

    /// <summary>
    /// One level of local moving: every node, in order, moves to the neighbouring community that raises modularity most,
    /// until a sweep moves nobody.
    /// </summary>
    /// <param name="nodes">The level's nodes and their neighbours.</param>
    /// <param name="weights">The weight of each of those neighbour entries, parallel to <paramref name="nodes"/>.</param>
    /// <param name="loops">Each node's internal weight, which is the first level's zeros and every level after it's own.</param>
    /// <param name="cancellationToken">Cancels between sweeps.</param>
    /// <returns>The community of each node of this level, as a node index.</returns>
    /// <remarks>
    /// The comparison is against the community the node is in, not against zero. Moving a node out of its community and
    /// into a neighbour's is a gain of <c>w(i, C) − d_C·k_i / 2m</c>; leaving the node where it is has a gain of
    /// <c>w(i, own) − d_own·k_i / 2m</c>, and comparing candidates against zero instead lets a node accept a move that
    /// is worse than standing still, which never settles. That was the first probe's 64-sweep churn.
    /// </remarks>
    private static int[] Sweep(
        int[][] nodes,
        double[][] weights,
        double[] loops,
        CancellationToken cancellationToken)
    {
        var count = nodes.Length;
        var degree = new double[count];
        for (var node = 0; node < count; node++)
        {
            degree[node] = (2 * loops[node]) + weights[node].Sum();
        }

        var total = degree.Sum() / 2.0;
        var partition = Enumerable.Range(0, count).ToArray();

        if (total <= 0)
        {
            return partition;
        }

        var communitySize = (double[])degree.Clone();
        var shared = new Dictionary<int, double>();
        var candidates = new List<int>();

        for (var sweep = 0; sweep < MaxSweeps; sweep++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var moved = false;

            for (var node = 0; node < count; node++)
            {
                var own = partition[node];
                var weight = degree[node];
                communitySize[own] -= weight;

                shared.Clear();
                candidates.Clear();
                for (var index = 0; index < nodes[node].Length; index++)
                {
                    var community = partition[nodes[node][index]];
                    if (!shared.TryAdd(community, weights[node][index]))
                    {
                        shared[community] += weights[node][index];
                    }
                    else
                    {
                        candidates.Add(community);
                    }
                }

                // Ascending, so an exact tie between two candidates breaks the same way in every process.
                candidates.Sort();

                var best = own;
                var bestGain = shared.GetValueOrDefault(own) - (communitySize[own] * weight / (2.0 * total));

                foreach (var community in candidates)
                {
                    var gain = shared[community] - (communitySize[community] * weight / (2.0 * total));
                    if (gain > bestGain + Tolerance)
                    {
                        bestGain = gain;
                        best = community;
                    }
                }

                partition[node] = best;
                communitySize[best] += weight;

                if (best != own)
                {
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        return partition;
    }

    /// <summary>
    /// Renumbers a partition's community ids to <c>0..k−1</c> in the order the ids first appear, so a level's answer can
    /// be the next level's node ids.
    /// </summary>
    /// <param name="partition">The community of each node, as a node index.</param>
    /// <param name="merged">How many communities the partition has.</param>
    /// <returns>The new id of each old id; entries the partition does not use are <c>-1</c>.</returns>
    private static int[] Dense(int[] partition, out int merged)
    {
        var map = new int[partition.Length];
        Array.Fill(map, -1);
        merged = 0;

        foreach (var community in partition)
        {
            if (map[community] < 0)
            {
                map[community] = merged++;
            }
        }

        return map;
    }

    /// <summary>
    /// Builds the next level: one node per community, edge weights summed, and the weight inside a community kept as
    /// that node's self loop.
    /// </summary>
    /// <param name="nodes">The level that was just partitioned.</param>
    /// <param name="weights">Its weights, parallel to its neighbour arrays.</param>
    /// <param name="loops">Its self loops.</param>
    /// <param name="partition">Its partition, as a node index per node.</param>
    /// <param name="map">The dense renumbering of <paramref name="partition"/>.</param>
    /// <param name="merged">How many nodes the next level has.</param>
    /// <returns>The next level's nodes, weights and self loops.</returns>
    /// <remarks>
    /// A self loop carries the weight a node now has inside itself, which is a community's internal weight once it
    /// becomes a node. Dropping it would silently change the objective: the second term of the formula counts degrees,
    /// and an internal edge is two degrees.
    /// </remarks>
    private static (int[][] Nodes, double[][] Weights, double[] Loops) Coarsen(
        int[][] nodes,
        double[][] weights,
        double[] loops,
        int[] partition,
        int[] map,
        int merged)
    {
        var mergedLoops = new double[merged];
        var links = new Dictionary<(int From, int To), double>();

        for (var node = 0; node < nodes.Length; node++)
        {
            var from = map[partition[node]];
            mergedLoops[from] += loops[node];

            for (var index = 0; index < nodes[node].Length; index++)
            {
                var to = map[partition[nodes[node][index]]];
                if (from == to)
                {
                    mergedLoops[from] += weights[node][index] / 2.0;
                    continue;
                }

                var pair = (from, to);
                links[pair] = links.GetValueOrDefault(pair) + weights[node][index];
            }
        }

        // Sorted rather than in dictionary order, for the same reason the candidates are: the next level's adjacency
        // decides the sweep, so its shape has to be a function of the graph and nothing else.
        var ordered = links
            .OrderBy(pair => pair.Key.From)
            .ThenBy(pair => pair.Key.To)
            .ToArray();

        var neighbours = new List<(int To, double Weight)>[merged];
        for (var node = 0; node < merged; node++)
        {
            neighbours[node] = [];
        }

        foreach (var (key, weight) in ordered)
        {
            neighbours[key.From].Add((key.To, weight));
        }

        return (
            [.. neighbours.Select(entries => entries.Select(entry => entry.To).ToArray())],
            [.. neighbours.Select(entries => entries.Select(entry => entry.Weight).ToArray())],
            mergedLoops);
    }
}
