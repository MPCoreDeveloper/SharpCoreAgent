using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Indexing.Embedding;
using SharpCoreAgent.Indexing.Retrieval;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The vector leg: cosine over the snapshot's own vectors, a floor that keeps orthogonal passages out of the
/// candidate list, and a note — never an exception — when the index cannot answer this way.
/// </summary>
public sealed class VectorRetrieverTests
{
    [Fact]
    public async Task FindsThePassageAboutTheQuestion()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        var result = VectorRetriever.Search(
            await LoadAsync(sandbox),
            HashedCodeEmbedder.CreateDefault(),
            "circle radius",
            5);

        Assert.Null(result.Note);
        Assert.NotEmpty(result.Matches);
        Assert.Equal("src/Geometry/Circle.cs", result.Matches[0].Chunk.Path);
        Assert.All(result.Matches, match => Assert.InRange(match.Score, 0d, 1d));
    }

    [Fact]
    public async Task ScoresNeverIncrease()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        var matches = VectorRetriever.Search(
            await LoadAsync(sandbox),
            HashedCodeEmbedder.CreateDefault(),
            "shape area circle",
            10).Matches;

        Assert.True(matches.Count > 1);
        for (var index = 1; index < matches.Count; index++)
        {
            Assert.True(matches[index - 1].Score >= matches[index].Score);
        }
    }

    [Fact]
    public async Task UnknownWordFallsBelowTheFloor()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        // Hashed vectors of unrelated text sit near zero; the floor is what turns that into "no match" instead of
        // candidates that only outrank each other.
        Assert.Empty(VectorRetriever.Search(
            await LoadAsync(sandbox),
            HashedCodeEmbedder.CreateDefault(),
            "xylophone",
            5).Matches);
    }

    [Fact]
    public async Task LegOffersMoreCandidatesThanTheCallerAsked()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        var matches = VectorRetriever.Search(
            await LoadAsync(sandbox),
            HashedCodeEmbedder.CreateDefault(),
            "radius area shape",
            1).Matches;

        Assert.InRange(matches.Count, 1, VectorRetriever.CandidateMultiplier);
    }

    [Fact]
    public async Task IndexWithoutVectorsYieldsANoteInsteadOfAnException()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await BuildAsync(sandbox);

        // What `--embedder off` leaves behind: rows without vectors, and the string "off" in meta.
        IndexTestDatabase.SetMeta(IndexPaths.For(sandbox.Workspace), "embedder", "off");

        var result = VectorRetriever.Search(LoadSnapshot(sandbox), HashedCodeEmbedder.CreateDefault(), "radius", 5);

        Assert.Empty(result.Matches);
        Assert.Contains("embedder off", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MismatchedDimensionsYieldsANote()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        var result = VectorRetriever.Search(await LoadAsync(sandbox), new HashedCodeEmbedder(256), "radius", 5);

        Assert.Empty(result.Matches);
        Assert.Contains("512", result.Note, StringComparison.Ordinal);
        Assert.Contains("256", result.Note, StringComparison.Ordinal);
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

    private const string NotesSource = """
        # Wildlife

        A kangaroo carries a joey; the herd moves at dawn.
        """;

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write("src/Geometry/Shape.cs", ShapeSource);
        sandbox.Write("src/Geometry/Circle.cs", CircleSource);
        sandbox.Write("docs/wildlife.md", NotesSource);
    }

    private static async Task<IndexSnapshot> LoadAsync(ToolTestWorkspace sandbox)
    {
        await BuildAsync(sandbox);
        return LoadSnapshot(sandbox);
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

