using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Ranking;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The whole search path through the adapter: two legs, fusion, ranking and the budget, answering from one snapshot.
/// </summary>
public sealed class CodeIndexSearchTests
{
    [Fact]
    public async Task SearchAnswersFromTheBuiltIndex()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        var blocks = index.Search(
            CodeQuery.Create("Circumference", k: 5),
            DateTimeOffset.UnixEpoch,
            EvidenceBudget.DefaultMaxChars);

        Assert.NotEmpty(blocks);
        Assert.Equal("src/Geometry/Circle.cs", blocks[0].Path);
        Assert.Equal("Demo.Geometry.Circle.Circumference", blocks[0].Symbol);
        Assert.True(blocks[0].Score > 0);
    }

    [Fact]
    public void SearchWithoutAnIndexIsEmpty()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);

        Assert.Empty(index.Search(CodeQuery.Create("Circle"), DateTimeOffset.UnixEpoch, EvidenceBudget.DefaultMaxChars));
    }

    [Fact]
    public async Task SearchRespectsTheCharacterBudget()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        using var index = SharpCoreDbCodeIndex.For(sandbox.Workspace);
        var blocks = index.Search(CodeQuery.Create("circle radius area shape"), DateTimeOffset.UnixEpoch, maxChars: 200);

        Assert.True(blocks.Count <= 2);
    }

    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle
        {
            private readonly double _radius;

            public double Circumference() => 2 * 3.14 * _radius;
        }
        """;

    private const string SquareSource = """
        namespace Demo.Geometry;

        public sealed class Square
        {
            public double Side { get; init; }
        }
        """;

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("src/Geometry/Square.cs", SquareSource);
    }

    private static async Task BuildAsync(ToolTestWorkspace sandbox)
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
    }
}
