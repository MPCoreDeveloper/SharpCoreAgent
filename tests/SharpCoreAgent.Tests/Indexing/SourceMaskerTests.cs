using SharpCoreAgent.Indexing.Extraction;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The masker that keeps prose out of call detection while preserving every offset and line break.
/// </summary>
/// <remarks>
/// The property that matters is not "the comment is gone" but "the code around it is still where it was",
/// because a range computed from a masked match is reported to a reader as a citation.
/// </remarks>
public sealed class SourceMaskerTests
{
    [Fact]
    public void ALineCommentIsBlankedAndTheLineKept()
    {
        const string source = "var x = 1; // calls RefreshAsync()\nvar y = 2;";

        var masked = SourceMasker.Mask(source);

        Assert.Equal(source.Length, masked.Length);
        Assert.Equal(source.Count(c => c == '\n'), masked.Count(c => c == '\n'));
        Assert.Contains("var x = 1;", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshAsync", masked, StringComparison.Ordinal);
        Assert.Contains("var y = 2;", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockCommentIsBlankedAcrossLines()
    {
        const string source = "a(); /* tricky\nstill tricky */ b();";

        var masked = SourceMasker.Mask(source);

        Assert.Equal(source.Length, masked.Length);
        Assert.DoesNotContain("tricky", masked, StringComparison.Ordinal);
        Assert.Contains("a();", masked, StringComparison.Ordinal);
        Assert.Contains("b();", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void AHashLineIsBlankedBecauseItIsACommentOrADirective()
    {
        const string source = "# see FakeCall()\nvalue = 1";

        var masked = SourceMasker.Mask(source);

        Assert.DoesNotContain("FakeCall", masked, StringComparison.Ordinal);
        Assert.Contains("value = 1", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void AStringLiteralIsBlankedSoItsWordsAreNotCalls()
    {
        const string source = "Log(\"call DoWork() now\");";

        var masked = SourceMasker.Mask(source);

        Assert.DoesNotContain("DoWork", masked, StringComparison.Ordinal);
        Assert.Contains("Log(", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEscapedQuoteDoesNotEndTheLiteral()
    {
        const string source = """var s = "a \" DoWork() b"; After();""";

        var masked = SourceMasker.Mask(source);

        Assert.DoesNotContain("DoWork", masked, StringComparison.Ordinal);
        Assert.Contains("After", masked, StringComparison.Ordinal);
        Assert.Equal(source.Length, masked.Length);
    }

    [Fact]
    public void AnUnterminatedLiteralStopsAtItsLine()
    {
        const string source = "var s = \"unterminated\nNext();";

        var masked = SourceMasker.Mask(source);

        Assert.Contains("Next();", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockStringIsBlankedToItsClosingTripleQuote()
    {
        const string source = "var raw = \"\"\"\nInside()\n\"\"\"; Outside();";

        var masked = SourceMasker.Mask(source);

        Assert.DoesNotContain("Inside", masked, StringComparison.Ordinal);
        Assert.Contains("Outside", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnbalancedBlockCommentRunsToTheEnd()
    {
        const string source = "Call(); /* forever";

        var masked = SourceMasker.Mask(source);

        Assert.Contains("Call();", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("forever", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void NullTextIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => SourceMasker.Mask(null!));
}
