using SharpCoreAgent.Indexing.Retrieval;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The documentation retriever: the literal match, the heading-first order, the cap and the pass-through of the
/// declaration a section describes.
/// </summary>
public sealed class DocumentationRetrieverTests
{
    [Fact]
    public void EveryTermMustAppear()
    {
        DocRow[] docs =
        [
            Doc("a.md", "Shapes", "A circle with a radius."),
            Doc("b.md", "Circles", "Just a circle."),
        ];

        var hits = DocumentationRetriever.Search(docs, "circle radius", 10);

        Assert.Single(hits);
        Assert.Equal("a.md", hits[0].Path);
    }

    [Fact]
    public void HeadingMatchRanksFirst()
    {
        DocRow[] docs =
        [
            Doc("body.md", "Notes", "The retry policy is described here."),
            Doc("heading.md", "Retry policy", "Short."),
        ];

        var hits = DocumentationRetriever.Search(docs, "retry", 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal("heading.md", hits[0].Path);
    }

    [Fact]
    public void SameQuerySameOrder()
    {
        DocRow[] docs =
        [
            Doc("b.md", null, "loop"),
            Doc("a.md", null, "loop"),
            Doc("c.md", null, "loop"),
        ];

        var first = DocumentationRetriever.Search(docs, "loop", 10);
        var second = DocumentationRetriever.Search(docs, "loop", 10);

        Assert.Equal(first.Select(doc => doc.Path), second.Select(doc => doc.Path));
        Assert.Equal(["a.md", "b.md", "c.md"], first.Select(doc => doc.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankQueryReturnsEmpty(string query)
    {
        Assert.Empty(DocumentationRetriever.Search([Doc("a.md", "H", "text")], query, 10));
    }

    [Fact]
    public void CapIsApplied()
    {
        var docs = Enumerable.Range(0, 5).Select(index => Doc($"d{index}.md", null, "term")).ToArray();

        Assert.Equal(2, DocumentationRetriever.Search(docs, "term", 2).Count);
    }

    [Fact]
    public void SectionWithoutAMatchIsDropped()
    {
        Assert.Empty(DocumentationRetriever.Search([Doc("a.md", "Totals", "Sums values.")], "totally unrelated", 10));
    }

    [Fact]
    public void HeadingAndDescribedSymbolArePreserved()
    {
        var hits = DocumentationRetriever.Search(
            [Doc("x.md", "Total", "Explains Total.", symbol: "Cart.Total")],
            "total",
            10);

        Assert.Equal("Total", hits[0].Heading);
        Assert.Equal("Cart.Total", hits[0].Symbol);
        Assert.Equal(1, hits[0].StartLine);
        Assert.Equal(2, hits[0].EndLine);
    }

    private static DocRow Doc(string path, string? heading, string text, string? symbol = null) => new()
    {
        Path = path,
        Heading = heading,
        Symbol = symbol,
        StartLine = 1,
        EndLine = 2,
        Text = text,
    };
}
