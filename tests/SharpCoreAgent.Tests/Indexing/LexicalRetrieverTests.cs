using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Retrieval;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The lexical leg: BM25 over the passages of one build, with the two properties the ranker relies on — an exact
/// identifier reaches the passage that declares it, and a query with no signal returns nothing at all.
/// </summary>
public sealed class LexicalRetrieverTests
{
    [Fact]
    public async Task ExactIdentifierFindsItsDeclaration()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        var snapshot = await LoadAsync(sandbox);

        var matches = LexicalRetriever.Search(snapshot, "Area", 5);

        // BM25 alone weighs a short declaration against a long file; that the *declaration* ends up first is the
        // ranker's job (T019's exact-symbol boost), so this leg promises the weaker, testable property: the
        // passage that holds the declaration is among the matches.
        Assert.NotEmpty(matches);
        Assert.Contains(matches, match =>
            match.Chunk.Path.EndsWith("Circle.cs", StringComparison.Ordinal)
            && match.Chunk.Text.Contains("Area()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopWordOnlyQueryReturnsNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        var snapshot = await LoadAsync(sandbox);

        // Every word here is on the analyzer's stop list, so answering would mean matching every passage.
        Assert.Empty(LexicalRetriever.Search(snapshot, "how does this work", 5));
    }

    [Fact]
    public async Task ScoresNeverIncrease()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        var snapshot = await LoadAsync(sandbox);

        var matches = LexicalRetriever.Search(snapshot, "area radius circle", 10);

        Assert.True(matches.Count > 1);
        for (var index = 1; index < matches.Count; index++)
        {
            Assert.True(matches[index - 1].Score >= matches[index].Score);
        }
    }

    [Fact]
    public async Task LegOffersMoreCandidatesThanTheCallerAsked()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        var snapshot = await LoadAsync(sandbox);

        // Four times k, capped by what the corpus can match: the fusion step needs room to reorder.
        var matches = LexicalRetriever.Search(snapshot, "shape", 1);

        Assert.InRange(matches.Count, 1, LexicalRetriever.CandidateMultiplier);
    }

    [Fact]
    public async Task UnknownWordFindsNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        var snapshot = await LoadAsync(sandbox);

        Assert.Empty(LexicalRetriever.Search(snapshot, "kangaroo", 5));
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
        }
        """;

    private const string ReadmeSource = """
        # Geometry

        A circle has a radius; these notes describe the shapes of this workspace.
        """;

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Shape.cs", ShapeSource);
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("docs/geometry.md", ReadmeSource);
    }

    private static async Task<IndexSnapshot> LoadAsync(ToolTestWorkspace sandbox)
    {
        await using (var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace))
        {
            var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
        }

        using var lease = IndexHandleCache.Acquire(
            IndexPaths.For(sandbox.Workspace),
            encryptionPassword: null,
            createIfMissing: false);

        return IndexReader.Create().Load(lease.Database.Database);
    }
}
