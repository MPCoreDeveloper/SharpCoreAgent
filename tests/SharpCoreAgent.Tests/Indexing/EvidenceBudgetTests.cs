using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Ranking;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The budget: twelve blocks, two per file, sixteen hundred characters per excerpt, and a note when something was
/// left out.
/// </summary>
public sealed class EvidenceBudgetTests
{
    [Fact]
    public void NeverReturnsMoreThanTwelveBlocks()
    {
        var result = EvidenceBudget.Apply(Candidates(40, "src/File{0}.cs"), EvidenceBudget.DefaultMaxChars);

        Assert.Equal(EvidenceBudget.MaxBlocks, result.Blocks.Count);
        Assert.Equal(EvidenceBudget.MaxBlocks, result.Blocks.Select(block => block.Path).Distinct().Count());
        Assert.Equal(28, result.DroppedByCount);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void OneFileContributesTwoBlocks()
    {
        var result = EvidenceBudget.Apply(Candidates(9, "src/Same.cs", varyPath: false), EvidenceBudget.DefaultMaxChars);

        Assert.Equal(EvidenceBudget.MaxBlocksPerFile, result.Blocks.Count);
        Assert.All(result.Blocks, block => Assert.Equal("src/Same.cs", block.Path));
        Assert.Equal(7, result.DroppedByFileCap);
    }

    [Fact]
    public void ExcerptsAreCutAtSixteenHundredCharacters()
    {
        var block = Evidence("src/Long.cs", new string('x', 4000), score: 1);
        var result = EvidenceBudget.Apply([block], EvidenceBudget.DefaultMaxChars);

        var only = Assert.Single(result.Blocks);
        Assert.True(only.Text.Length <= EvidenceBudget.MaxExcerptChars);
        Assert.Equal(1, result.Trimmed);
    }

    [Fact]
    public void ExcerptsCutOnALineBoundary()
    {
        var text = string.Join('\n', Enumerable.Repeat(new string('y', 60), 100));
        var result = EvidenceBudget.Apply([Evidence("src/Lines.cs", text, score: 1)], EvidenceBudget.DefaultMaxChars);

        Assert.DoesNotContain('\n', result.Blocks[0].Text[^1..]);
        Assert.EndsWith("y", result.Blocks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterBudgetDropsWhatDoesNotFit()
    {
        var result = EvidenceBudget.Apply(Candidates(6, "src/File{0}.cs"), maxChars: 600);

        Assert.True(result.Blocks.Count < 6);
        Assert.True(result.DroppedBySize > 0);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void NothingDroppedMeansNoNote()
    {
        var result = EvidenceBudget.Apply(Candidates(3, "src/File{0}.cs"), EvidenceBudget.DefaultMaxChars);

        Assert.Equal(3, result.Blocks.Count);
        Assert.Equal(0, result.Dropped);
        Assert.Null(result.Note);
    }

    [Fact]
    public void RankOrderSurvivesTheBudget()
    {
        var ranked = Candidates(5, "src/File{0}.cs");
        var result = EvidenceBudget.Apply(ranked, EvidenceBudget.DefaultMaxChars);

        Assert.Equal(
            ranked.Take(5).Select(block => block.Sha8),
            result.Blocks.Select(block => block.Sha8));
    }

    private static List<CodeEvidence> Candidates(int count, string pattern, bool varyPath = true) =>
        [.. Enumerable.Range(0, count)
            .Select(index => Evidence(
                varyPath ? string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern, index) : pattern,
                $"passage {index}",
                1d - (index * 0.01)))];

    private static CodeEvidence Evidence(string path, string text, double score) => new()
    {
        Path = path,
        StartLine = 1,
        EndLine = 2,
        Sha8 = "0123abcd",
        Text = text,
        Kind = "chunk",
        Score = score,
        Legs = [EvidenceLeg.Lexical],
    };
}
