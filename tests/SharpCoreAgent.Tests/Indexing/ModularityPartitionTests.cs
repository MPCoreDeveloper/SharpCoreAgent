using SharpCoreAgent.Indexing.Graph;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The partition on its own: does it answer the question modularity asks, and does it answer the same way twice?
/// </summary>
/// <remarks>
/// The graphs here are small enough to reason about by hand and shaped so that a plausible wrong answer is visible: a
/// partition that labelled every node separately would satisfy "every node has a label" and fail every case below, and
/// one that labelled everything together would fail the first two.
/// </remarks>
public sealed class ModularityPartitionTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public void TwoClustersThatNeverMeetAreTwoCommunities()
    {
        // Two triangles, 0-1-2 and 3-4-5, with nothing between them: the classic answer is two communities of three.
        var membership = ModularityPartition.Nodes(
            [
                [1, 2],
                [0, 2],
                [0, 1],
                [4, 5],
                [3, 5],
                [3, 4],
            ],
            Token);

        var communities = Communities(membership);

        Assert.Equal(2, communities.Count);
        Assert.All(communities, community => Assert.Equal(3, community.Length));
        Assert.Equal(membership[0], membership[2]);
        Assert.NotEqual(membership[0], membership[3]);
    }

    [Fact]
    public void ATrianglePairJoinedByOneEdgeIsStillTwoCommunities()
    {
        // The same two triangles plus one relation between node 2 and node 3. Cutting that edge costs less modularity
        // than merging six declarations into one cluster, which is the difference between this and a connected
        // components walk wearing a partition's name.
        var membership = ModularityPartition.Nodes(
            [
                [1, 2],
                [0, 2],
                [0, 1, 3],
                [2, 4, 5],
                [3, 5],
                [3, 4],
            ],
            Token);

        Assert.Equal(2, Communities(membership).Count);
        Assert.Equal(membership[0], membership[2]);
        Assert.NotEqual(membership[2], membership[3]);
        Assert.Equal(membership[4], membership[5]);
    }

    [Fact]
    public void AnEdgeIsOneCommunityAndALoneNodeIsItsOwn()
    {
        var pair = ModularityPartition.Nodes([[1], [0]], Token);
        Assert.Single(Communities(pair));

        var alone = ModularityPartition.Nodes([[]], Token);
        Assert.Single(Communities(alone));

        Assert.Empty(ModularityPartition.Nodes([], Token));
    }

    [Fact]
    public void ASingleMoveNeverImprovesThePartitionItReturns()
    {
        // The invariant the sweep is built on: the answer is a local optimum of the definition, so no node can be moved
        // into a neighbouring community for a gain. Proven here by trying every such move and scoring it.
        int[][] adjacency =
        [
            [1, 2, 5],
            [0, 2],
            [0, 1],
            [4, 5, 0],
            [3, 5],
            [3, 4, 0],
            [7],
            [6],
        ];

        var membership = ModularityPartition.Nodes(adjacency, Token);
        var modularity = Modularity(adjacency, membership);

        for (var node = 0; node < adjacency.Length; node++)
        {
            foreach (var neighbour in adjacency[node])
            {
                if (membership[neighbour] == membership[node])
                {
                    continue;
                }

                var moved = (int[])membership.Clone();
                moved[node] = membership[neighbour];

                Assert.True(
                    Modularity(adjacency, moved) <= modularity + 1e-9,
                    $"moving node {node} into community {membership[neighbour]} raised modularity from {modularity} to {Modularity(adjacency, moved)}");
            }
        }
    }

    [Fact]
    public void TheSameGraphTwiceIsTheSamePartition()
    {
        var adjacency = RingOfCliques(cliques: 4, size: 5);

        Assert.Equal(
            ModularityPartition.Nodes(adjacency, Token),
            ModularityPartition.Nodes(adjacency, Token));
    }

    [Fact]
    public void TheSecondPhaseMergesCliquesThatOneSweepWouldLeaveApart()
    {
        // Twenty triangles in a ring. A single local-moving pass settles on the twenty triangles and stops, because no
        // node inside one has a reason to leave it: leaving costs two internal edges and gains one. Only the level
        // above — twenty triangles as twenty nodes in a ring — can see that merging neighbours is worth it, since the
        // gain of joining two communities is w − d_A·d_B/2m, which here is 1 − 81/160.
        var adjacency = RingOfCliques(cliques: 20, size: 3);

        var communities = Communities(ModularityPartition.Nodes(adjacency, Token));

        Assert.True(
            communities.Count < 20,
            $"the ring of twenty triangles came back as {communities.Count} communities, so no level after the first merged anything");
        Assert.Equal(adjacency.Length, communities.Sum(community => community.Length));
    }

    /// <summary>Builds <paramref name="cliques"/> cliques of <paramref name="size"/> nodes, each joined to the next.</summary>
    private static int[][] RingOfCliques(int cliques, int size)
    {
        var adjacency = new List<int>[cliques * size];
        for (var node = 0; node < adjacency.Length; node++)
        {
            adjacency[node] = [];
        }

        for (var clique = 0; clique < cliques; clique++)
        {
            for (var left = 0; left < size; left++)
            {
                for (var right = left + 1; right < size; right++)
                {
                    var a = (clique * size) + left;
                    var b = (clique * size) + right;
                    adjacency[a].Add(b);
                    adjacency[b].Add(a);
                }
            }

            // One edge to the next clique, and the ring closes: every clique is attached on both sides.
            var here = clique * size;
            var next = ((clique + 1) % cliques) * size;
            adjacency[here].Add(next);
            adjacency[next].Add(here);
        }

        return [.. adjacency.Select(neighbours => neighbours.Order().ToArray())];
    }

    /// <summary>The definition the partition optimises: <c>Q = Σ_c [l_c / m − (d_c / 2m)²]</c>.</summary>
    private static double Modularity(int[][] adjacency, int[] membership)
    {
        var edges = adjacency.Sum(neighbours => neighbours.Length) / 2.0;
        if (edges == 0)
        {
            return 0;
        }

        var inside = new Dictionary<int, int>();
        var degrees = new Dictionary<int, int>();

        for (var node = 0; node < adjacency.Length; node++)
        {
            degrees[membership[node]] = degrees.GetValueOrDefault(membership[node]) + adjacency[node].Length;

            foreach (var neighbour in adjacency[node])
            {
                if (membership[neighbour] == membership[node] && node < neighbour)
                {
                    inside[membership[node]] = inside.GetValueOrDefault(membership[node]) + 1;
                }
            }
        }

        var total = 0.0;
        foreach (var (community, degree) in degrees)
        {
            total += (inside.GetValueOrDefault(community) / edges) - Math.Pow(degree / (2.0 * edges), 2);
        }

        return total;
    }

    /// <summary>Groups nodes by their community, ascending, for the assertions above.</summary>
    private static List<int[]> Communities(int[] membership) =>
    [
        .. membership
            .Select((community, node) => (Community: community, Node: node))
            .GroupBy(entry => entry.Community)
            .Select(group => group.Select(entry => entry.Node).Order().ToArray())
            .OrderBy(nodes => nodes[0]),
    ];
}
