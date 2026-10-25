using SharpCoreAgent.Indexing.Extraction;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The tokenizer is the contract between what a file says and what a query can find, so these tests are about
/// the splits: where a word boundary falls, what is dropped, and the fact that none of it drifts between calls.
/// </summary>
public sealed class CodeTokenizerTests
{
    [Theory]
    [InlineData("RunTurnAsync", "run turn async")]
    [InlineData("IModelClient", "i model client")]
    [InlineData("turn_async", "turn async")]
    [InlineData("HTTPServer", "http server")]
    [InlineData("ISyntaxFactsExtractor", "i syntax facts extractor")]
    [InlineData("base64Encode", "base 64 encode")]
    [InlineData("SharpCoreDbIndexMaintainer.EnsureFreshAsync", "sharp core db index maintainer ensure fresh async")]
    [InlineData("v2Api", "v 2 api")]
    public void SplitsIdentifiersIntoTheWordsAQueryWouldUse(string text, string expected) =>
        Assert.Equal(expected, CodeTokenizer.TokenizeForIndex(text));

    [Fact]
    public void DropsPunctuationAndLoneShortWords()
    {
        // "a", "x" and "1" are dropped; the words around them are not.
        Assert.Equal("return value", CodeTokenizer.TokenizeForIndex("return (a + x) * 1; // value"));

        // A single letter inside a name is a component, not noise.
        Assert.Equal("i model client", CodeTokenizer.TokenizeForIndex("IModelClient"));
    }

    [Fact]
    public void KeepsRepetitionsInIndexedTextAndDropsThemFromQueries()
    {
        // Term frequency is what BM25 reads, so the document side keeps every occurrence.
        Assert.Equal("parse parse parse", CodeTokenizer.TokenizeForIndex("Parse(); parse(); PARSE();"));

        // A query that repeats a word does not weight it three times.
        Assert.Equal("parse", CodeTokenizer.TokenizeForQuery("parse parse PARSE"));
    }

    [Fact]
    public void DropsStopWordsFromQueriesButNotFromIndexedText()
    {
        Assert.Equal("where the tokenizer splits identifiers", CodeTokenizer.TokenizeForIndex(
            "Where the tokenizer splits identifiers"));

        Assert.Equal("tokenizer split identifiers", CodeTokenizer.TokenizeForQuery(
            "Where does the tokenizer split identifiers?"));
    }

    [Fact]
    public void ReturnsAnEmptyStringWhenThereIsNothingToIndex()
    {
        Assert.Equal(string.Empty, CodeTokenizer.TokenizeForIndex(string.Empty));
        Assert.Equal(string.Empty, CodeTokenizer.TokenizeForIndex("   \r\n\t /* */ => != "));
        Assert.Equal(string.Empty, CodeTokenizer.TokenizeForQuery("what is the a an of to"));
        Assert.Empty(CodeTokenizer.Split("(){}[]"));
    }

    [Fact]
    public void IsStableAcrossCalls()
    {
        const string Text = """
            public sealed class SharpCoreDbCodeIndex : ICodeIndex
            {
                private readonly IndexHandleCache _cache = IndexHandleCache.For(workspace: 1);

                public Task<IndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
                    Task.FromResult(IndexStatus.Missing(IndexFreshness.Missing));
            }
            """;

        var first = CodeTokenizer.TokenizeForIndex(Text);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(first, CodeTokenizer.TokenizeForIndex(Text));
        }

        Assert.Contains("sharp core db code index", first, StringComparison.Ordinal);
        Assert.Contains("index handle cache", first, StringComparison.Ordinal);
        Assert.Contains("index status", first, StringComparison.Ordinal);
        Assert.DoesNotContain("ICodeIndex".ToLowerInvariant(), first, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => CodeTokenizer.Split(null!));
        Assert.Throws<ArgumentNullException>(() => CodeTokenizer.TokenizeForIndex(null!));
        Assert.Throws<ArgumentNullException>(() => CodeTokenizer.TokenizeForQuery(null!));
    }
}
