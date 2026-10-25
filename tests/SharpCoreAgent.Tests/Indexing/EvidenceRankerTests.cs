using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Embedding;
using SharpCoreAgent.Indexing.Ranking;
using SharpCoreAgent.Indexing.Retrieval;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The ranker over a real index: an exact identifier reaches its declaration, a type name reaches the type, graph
/// neighbours arrive labelled, and the filters and the ordering behave the way a caller can rely on.
/// </summary>
public sealed class EvidenceRankerTests
{
    [Fact]
    public async Task ExactIdentifierRanksItsDeclarationFirst()
    {
        var blocks = await RankAsync(CodeQuery.Create("Circumference", k: 5, hops: 0));

        var first = blocks[0];
        Assert.Equal("src/Geometry/Circle.cs", first.Path);
        Assert.Equal("Demo.Geometry.Circle.Circumference", first.Symbol);
        Assert.Equal("method", first.Kind);
    }

    [Fact]
    public async Task TypeNameRanksTheTypeDeclarationFirst()
    {
        var blocks = await RankAsync(CodeQuery.Create("Circle", k: 5, hops: 0));

        Assert.Equal("src/Geometry/Circle.cs", blocks[0].Path);
        Assert.Equal("class", blocks[0].Kind);
        Assert.Equal("Demo.Geometry.Circle", blocks[0].Symbol);
    }

    [Fact]
    public async Task GraphNeighboursArriveWithTheirRelation()
    {
        // "Circle" never mentions "Shape" in the query, so a block from Shape.cs can only come from the graph: the
        // type is one 'inherits' edge away from the type the query matched.
        var blocks = await RankAsync(CodeQuery.Create("Circle", k: 10, hops: 1));

        var inherited = blocks.First(block => block.Path == "src/Geometry/Shape.cs");
        Assert.Contains(EvidenceLeg.Graph, inherited.Legs);
        Assert.Equal("inherits", inherited.Relation);
        Assert.True(inherited.Score > 0);
    }

    [Fact]
    public async Task PathFilterExcludesOtherFiles()
    {
        var blocks = await RankAsync(CodeQuery.Create("area", path: "src/Geometry/Circle.cs", k: 8));

        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.Equal("src/Geometry/Circle.cs", block.Path));
    }

    [Fact]
    public async Task KindFilterExcludesMembers()
    {
        // A type's own passage holds its header, so a query that names the type is what reaches it; a query about
        // what the type *does* matches its members, which is the answer a kind filter of `class` correctly refuses.
        var blocks = await RankAsync(CodeQuery.Create("Circle", kind: "class", k: 8, hops: 0));

        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.Equal("class", block.Kind));
    }

    [Fact]
    public async Task DocKindFindsDocumentation()
    {
        var blocks = await RankAsync(CodeQuery.Create("radius", kind: "doc", k: 8, hops: 0));

        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.Equal("doc", block.Kind));
    }

    [Fact]
    public async Task SameQuerySameOrder()
    {
        var first = await RankAsync(CodeQuery.Create("circle radius area", k: 8));
        var second = await RankAsync(CodeQuery.Create("circle radius area", k: 8));

        Assert.Equal(
            first.Select(block => (block.Path, block.StartLine, block.Score)),
            second.Select(block => (block.Path, block.StartLine, block.Score)));
    }

    [Fact]
    public async Task ScoresAreRoundedAndDescending()
    {
        var blocks = await RankAsync(CodeQuery.Create("area shape circle", k: 10));

        Assert.True(blocks.Count > 1);
        for (var index = 0; index < blocks.Count; index++)
        {
            Assert.Equal(Math.Round(blocks[index].Score, 6), blocks[index].Score, 6);

            if (index > 0)
            {
                Assert.True(blocks[index - 1].Score >= blocks[index].Score);
            }
        }
    }

    [Fact]
    public async Task StaleFileIsFlagged()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);
        IndexTestDatabase.Execute(
            IndexPaths.For(sandbox.Workspace),
            "UPDATE files SET state = 'stale' WHERE path = 'src/Geometry/Circle.cs'");

        var snapshot = LoadSnapshot(sandbox);
        var blocks = EvidenceRanker.Rank(
            snapshot,
            CodeQuery.Create("Circle", k: 5, hops: 0),
            LexicalRetriever.Search(snapshot, "Circle", 5),
            VectorRetriever.Search(snapshot, HashedCodeEmbedder.CreateDefault(), "Circle", 5).Matches,
            DateTimeOffset.UnixEpoch);

        var circle = blocks.First(block => block.Path == "src/Geometry/Circle.cs");
        Assert.True(circle.IsStale);
    }

    private const string ShapeSource = """
        namespace Demo.Geometry;

        public abstract class Shape
        {
            public abstract double Area();
        }
        """;

    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle : Shape
        {
            private readonly double _radius;

            public Circle(double radius) => _radius = radius;

            public override double Area() => 3.14 * _radius * _radius;

            public double Circumference() => 2 * 3.14 * _radius;
        }
        """;

    private const string SquareSource = """
        namespace Demo.Geometry;

        public sealed class Square : Shape
        {
            public double Side { get; init; }

            public override double Area() => Side * Side;
        }
        """;

    private const string DocSource = """
        # Geometry

        A Circle has a radius; the Square has a side.
        """;

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Shape.cs", ShapeSource);
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("src/Geometry/Square.cs", SquareSource);
        sandbox.Write("docs/geometry.md", DocSource);
    }

    private static async Task<IReadOnlyList<CodeEvidence>> RankAsync(CodeQuery query)
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        var snapshot = LoadSnapshot(sandbox);

        return EvidenceRanker.Rank(
            snapshot,
            query,
            LexicalRetriever.Search(snapshot, query.Text, query.K),
            VectorRetriever.Search(snapshot, HashedCodeEmbedder.CreateDefault(), query.Text, query.K).Matches,
            DateTimeOffset.UnixEpoch);
    }

    private static IndexSnapshot LoadSnapshot(ToolTestWorkspace sandbox)
    {
        using var lease = IndexHandleCache.Acquire(IndexPaths.For(sandbox.Workspace), null, createIfMissing: false);
        return IndexReader.Create().Load(lease.Database.Database);
    }

    private static async Task BuildAsync(ToolTestWorkspace sandbox)
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
    }
}
