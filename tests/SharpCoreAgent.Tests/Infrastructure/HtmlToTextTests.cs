using SharpCoreAgent.Infrastructure.Web;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The HTML reduction is what the model actually reads, so these tests pin the four decisions a reader would notice:
/// the elements that carry no prose are gone, entities become characters, blocks become lines, and whitespace stops
/// being significant.
/// </summary>
public sealed class HtmlToTextTests
{
    [Fact]
    public void KeepsTheProseAndDropsTheHeadScriptAndStyle() =>
        Assert.Equal(
            "Hello\nWorld",
            HtmlToText.Reduce(
                "<html><head><title>Title</title><style>p { color: red }</style></head><body><h1>Hello</h1><script>var x = 1;</script><p>World</p></body></html>"));

    [Fact]
    public void DecodesEntitiesAndLeavesEscapedMarkupAsText() =>
        Assert.Equal("a & b <b> c", HtmlToText.Reduce("<p>a &amp; b &lt;b&gt; c</p>"));

    [Fact]
    public void BreaksLinesAtBlockBoundaries()
    {
        Assert.Equal("one\ntwo", HtmlToText.Reduce("<div>one</div><div>two</div>"));
        Assert.Equal("one\ntwo", HtmlToText.Reduce("one<br/>two"));
    }

    [Fact]
    public void CollapsesRunsOfWhitespace() =>
        Assert.Equal("one two three", HtmlToText.Reduce("<p>one    two\r\n\tthree</p>"));

    [Fact]
    public void DropsEmptyLinesRatherThanPrintingBlankOnes() =>
        Assert.Equal("one\ntwo", HtmlToText.Reduce("<p>one</p>\n\n\n<p></p><p>two</p>"));

    [Fact]
    public void AGreaterThanInsideAnAttributeDoesNotEndTheTag() =>
        Assert.Equal("link", HtmlToText.Reduce("<a href=\"x > y\" title='a>b'>link</a>"));

    [Fact]
    public void DropsComments() => Assert.Equal("ab", HtmlToText.Reduce("a<!-- hidden -->b"));

    [Fact]
    public void StopsAtAnUnterminatedTag() => Assert.Equal("hello", HtmlToText.Reduce("hello <p"));

    [Fact]
    public void AnUnclosedScriptEndsTheReduction() =>
        Assert.Equal("before", HtmlToText.Reduce("before<script>var x = 1"));

    [Fact]
    public void SkipsNoscriptAndSvg() =>
        Assert.Equal("text", HtmlToText.Reduce("<noscript>enable js</noscript><svg><text>icon</text></svg>text"));

    [Fact]
    public void TextWithoutMarkupIsReturnedTrimmed() =>
        Assert.Equal("plain", HtmlToText.Reduce("  plain\n"));

    [Fact]
    public void AnEmptyDocumentReducesToNothing() => Assert.Equal(string.Empty, HtmlToText.Reduce(string.Empty));
}
