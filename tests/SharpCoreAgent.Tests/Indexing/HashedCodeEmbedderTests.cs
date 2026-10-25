using SharpCoreAgent.Indexing.Embedding;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The hashed embedder is the default, so its properties are load-bearing: a build has to reproduce the same
/// vectors, a vector has to be usable as a direction, and nothing about a file's content may throw.
/// </summary>
public sealed class HashedCodeEmbedderTests
{
    [Fact]
    public void ReportsTheIdentityTheSchemaStores()
    {
        var embedder = HashedCodeEmbedder.CreateDefault();

        Assert.Equal("hashed-local/512", embedder.Id);
        Assert.Equal(512, embedder.Dimensions);
        Assert.Equal(HashedCodeEmbedder.DefaultDimensions, embedder.Dimensions);

        // The id carries the width, so an index built by a different width cannot be mistaken for this one.
        Assert.Equal("hashed-local/64", new HashedCodeEmbedder(64).Id);
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashedCodeEmbedder(0));
    }

    [Fact]
    public void ProducesTheSameVectorFromEveryInstance()
    {
        const string Text = "public Task<IndexStatus> GetStatusAsync(CancellationToken cancellationToken)";

        var first = Embed(new HashedCodeEmbedder(), Text);
        var second = Embed(new HashedCodeEmbedder(), Text);

        Assert.Equal(first, second);

        // The same words in a different order are the same bag of words, so the vector is identical — exactly,
        // not approximately, because the embedder accumulates in ordinal token order.
        Assert.Equal(
            first,
            Embed(new HashedCodeEmbedder(), "CancellationToken Task<IndexStatus> GetStatusAsync public cancellationToken"));
    }

    [Fact]
    public void NormalisesToUnitLength()
    {
        var embedder = new HashedCodeEmbedder();
        var vector = Embed(embedder, "the index maintains a writer lock and releases it in DisposeAsync");

        var norm = MathF.Sqrt(vector.Sum(component => component * component));

        Assert.InRange(norm, 0.999f, 1.001f);
        Assert.Contains(vector, component => component != 0f);
    }

    [Fact]
    public void PointsUnrelatedTextsInDifferentDirections()
    {
        var embedder = new HashedCodeEmbedder();

        var embedder1 = Embed(embedder, "SharpCoreDbIndexMaintainer EnsureFreshAsync acquires the writer lock");
        var unrelated = Embed(embedder, "the passage chunker splits a file into ranges of at most forty lines");
        var related = Embed(embedder, "the maintainer acquires a writer lock before it builds the index");

        Assert.True(
            HashedCodeEmbedder.CosineSimilarity(embedder1, unrelated) < 0.2f,
            "unrelated passages should share almost no dimensions");

        Assert.True(
            HashedCodeEmbedder.CosineSimilarity(embedder1, related) >
            HashedCodeEmbedder.CosineSimilarity(embedder1, unrelated),
            "a passage that repeats the same identifiers must be closer than one that does not");
    }

    [Fact]
    public void WeighsTermFrequencyByOnePlusLog()
    {
        var embedder = new HashedCodeEmbedder();

        // One word, however often it repeats, is one direction once normalised. The sign is the hash's high bit,
        // so what this test can assert is the magnitude.
        var repeated = Embed(embedder, "chunker chunker");
        var single = Embed(embedder, "chunker");

        Assert.Equal(1f, repeated.Max(MathF.Abs), 4);
        Assert.Equal(1f, MathF.Abs(single.Max(MathF.Abs)), 4);
        Assert.Equal(0f, MathF.Abs(repeated.Sum(MathF.Abs)) - 1f, 4);

        // Two words still sum to one unit of length, whatever their signs.
        var pair = Embed(embedder, "chunker passage");
        Assert.Equal(1f, MathF.Sqrt(pair.Sum(component => component * component)), 4);
    }

    [Fact]
    public void ReturnsTheZeroVectorForTextWithNoWords()
    {
        var embedder = new HashedCodeEmbedder();

        foreach (var text in new[] { string.Empty, "   \r\n\t", "(){}[]; => != //", "a x 1" })
        {
            var vector = Embed(embedder, text);

            Assert.Equal(0f, vector.Max());
            Assert.Equal(0f, vector.Min());
        }

        // A zero vector is comparable rather than exceptional: it is simply never similar to anything.
        Assert.Equal(0f, HashedCodeEmbedder.CosineSimilarity(Embed(embedder, string.Empty), Embed(embedder, "index")));
    }

    [Fact]
    public void RefusesADestinationOfTheWrongWidth()
    {
        var embedder = new HashedCodeEmbedder();

        Assert.Throws<ArgumentException>(() => embedder.Embed("index", new float[511]));
        Assert.Throws<ArgumentNullException>(() => embedder.Embed(null!, new float[512]));
    }

    [Fact]
    public void CosineSimilarityRejectsMismatchedWidths()
    {
        Assert.Throws<ArgumentException>(
            () => HashedCodeEmbedder.CosineSimilarity(new float[4], new float[5]));
        Assert.Equal(0f, HashedCodeEmbedder.CosineSimilarity(new float[4], new float[4]));
    }

    private static float[] Embed(HashedCodeEmbedder embedder, string text)
    {
        var vector = new float[embedder.Dimensions];
        embedder.Embed(text, vector);
        return vector;
    }
}
