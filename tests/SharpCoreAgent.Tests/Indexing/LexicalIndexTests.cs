using SharpCoreAgent.Indexing.Retrieval;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The in-memory BM25 leg: what it does with no signal, no posting and more than one term.
/// </summary>
public sealed class LexicalIndexTests
{
    [Fact]
    public void AnEmptyQueryAndANegativeLimitReturnNothing()
    {
        var index = LexicalIndex.Build([(1, "alpha beta"), (2, "beta gamma")]);

        Assert.Empty(index.Search(string.Empty, 5));
        Assert.Empty(index.Search("alpha", 0));
        Assert.Equal(2, index.DocumentCount);
    }

    [Fact]
    public void AQueryWithNoPostingReturnsNothing()
    {
        var index = LexicalIndex.Build([(1, "alpha beta")]);

        Assert.Empty(index.Search("delta", 5));
    }

    [Fact]
    public void TheRarerTermOutranksTheCommonOne()
    {
        var index = LexicalIndex.Build(
        [
            (1, "common rare"),
            (2, "common"),
            (3, "common"),
            (4, "common"),
        ]);

        var hits = index.Search("common rare", 4);

        Assert.Equal(1, hits[0].Id);
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.Equal(4, hits.Count);
    }
}
