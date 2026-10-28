using SharpCoreAgent.Domain.Text;
using Xunit;

namespace SharpCoreAgent.Tests.Domain;

/// <summary>
/// The fragment ladder: which rung accepts a fragment, and what a caller must do with the answer.
/// </summary>
/// <remarks>
/// The ladder is the safety argument of the edit path, so each rung is pinned with a case that only it can
/// accept. The tests also pin what is <em>not</em> forgiven: a fragment that is absent stays absent, and an
/// ambiguous fragment is never narrowed to one occurrence.
/// </remarks>
public sealed class FragmentFinderTests
{
    [Fact]
    public void AVerbatimFragmentIsExact()
    {
        var matches = FragmentFinder.Find("one\ntwo\nthree\n", "two\n");

        var match = Assert.Single(matches);
        Assert.Equal(FragmentMatchKind.Exact, match.Kind);
        Assert.Equal(4, match.Index);
        Assert.Equal("TWO\n", match.Apply("TWO\n"));
    }

    [Fact]
    public void AnAmbiguousFragmentIsReportedEveryTimeItAppears()
    {
        var matches = FragmentFinder.Find("x\ny\nx\n", "x");

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal(FragmentMatchKind.Exact, match.Kind));
    }

    [Fact]
    public void MatchesAtOneRungDoNotOverlap()
    {
        // "aa" in "aaaa" is two non-overlapping matches, which is what the replacement count promises.
        Assert.Equal(2, FragmentFinder.Find("aaaa", "aa").Count);
    }

    [Fact]
    public void ALineFeedFragmentIsFoundInACrlfFileAndKeepsThatFilesEndings()
    {
        var matches = FragmentFinder.Find("one\r\ntwo\r\nthree\r\n", "two\n");

        var match = Assert.Single(matches);
        Assert.Equal(FragmentMatchKind.LineEndings, match.Kind);
        Assert.Equal(5, match.Index);
        Assert.Equal("TWO\r\n", match.Apply("TWO\n"));
    }

    [Fact]
    public void ACrlfFragmentIsFoundInALineFeedFile()
    {
        var matches = FragmentFinder.Find("one\ntwo\n", "two\r\n");

        var match = Assert.Single(matches);
        Assert.Equal(FragmentMatchKind.LineEndings, match.Kind);
        Assert.Equal("TWO\n", match.Apply("TWO\r\n"));
    }

    [Fact]
    public void TrailingWhitespaceIsForgivenWhenItIsTheOnlyDifference()
    {
        var matches = FragmentFinder.Find("alpha   \nbeta\t\n", "alpha\nbeta\n");

        var match = Assert.Single(matches);
        Assert.Equal(FragmentMatchKind.TrailingWhitespace, match.Kind);
        Assert.Equal(0, match.Index);
        Assert.Equal(15, match.Length);
        Assert.Equal("ALPHA\nBETA\n", match.Apply("ALPHA\nBETA\n"));
    }

    [Fact]
    public void IndentationIsForgivenAndTheReplacementTakesTheFilesIndentation()
    {
        const string Text = "class A\n{\n    void M()\n    {\n        int x = 1;\n    }\n}\n";
        const string Find = "void M()\n{\n    int x = 1;\n}\n";

        var matches = FragmentFinder.Find(Text, Find);

        var match = Assert.Single(matches);
        Assert.Equal(FragmentMatchKind.Indentation, match.Kind);
        Assert.Equal(
            "    void M()\n    {\n        int x = 2;\n    }\n",
            match.Apply("void M()\n{\n    int x = 2;\n}\n"));
    }

    [Fact]
    public void AReindentedReplacementKeepsBlankLinesBlank()
    {
        const string Text = "    if (x)\n    {\n        y();\n    }\n";
        var match = Assert.Single(FragmentFinder.Find(Text, "if (x)\n{\n    y();\n}\n"));

        Assert.Equal(
            "    if (x)\n    {\n\n        y();\n    }\n",
            match.Apply("if (x)\n{\n\n    y();\n}\n"));
    }

    [Fact]
    public void AStricterRungWinsOverALooserOneThatWouldMatchMore()
    {
        // Two lines contain the text verbatim, so the exact rung answers — and reports both, which is what
        // lets the tool refuse. The looser rungs are never consulted, so they cannot narrow the count.
        var matches = FragmentFinder.Find("var a = 1;\n    var a = 1;\n", "var a = 1;");

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal(FragmentMatchKind.Exact, match.Kind));
    }

    [Fact]
    public void AnAbsentFragmentIsNotGuessedAt()
    {
        Assert.Empty(FragmentFinder.Find("alpha\nbeta\n", "gamma\n"));
    }

    [Fact]
    public void AWhitespaceOnlyFragmentIsOnlyMatchedVerbatim()
    {
        // Loose matching of a blank fragment would match anywhere, so the ladder does not start for it.
        Assert.Empty(FragmentFinder.Find("alpha\n\nbeta\n", "   \n"));
    }

    [Fact]
    public void AnEmptyFragmentMatchesNothing()
    {
        Assert.Empty(FragmentFinder.Find("alpha\n", string.Empty));
    }

    [Fact]
    public void TheFragmentKeepsItsOwnFinalNewlineOnly()
    {
        // Without the terminator the match stops at the end of the line, so the newline in the file survives
        // and the model's replacement is spliced where the text was.
        var text = "one\ntwo\n";
        var match = Assert.Single(FragmentFinder.Find(text, "one"));

        Assert.Equal(3, match.Length);
        Assert.Equal("ONE\ntwo\n", string.Concat(text[..match.Index], match.Apply("ONE"), text[(match.Index + match.Length)..]));
    }

    [Fact]
    public void ANullArgumentIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => _ = FragmentFinder.Find(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => _ = FragmentFinder.Find("x", null!));
    }
}
