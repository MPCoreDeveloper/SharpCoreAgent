using System.Text;
using SharpCoreAgent.Indexing.Extraction;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// A passage is a citation, so the properties worth testing are geometric: the caps, the tiling, and the fact that
/// no range ever describes half a line.
/// </summary>
public sealed class PassageChunkerTests
{
    [Fact]
    public void ChunksAFourThousandLineBlockInsteadOfTruncatingIt()
    {
        var text = GeneratedBlock(4_000);

        var passages = Chunk(text, []);

        // 4 000 lines at 40 lines a passage is exactly 100 passages: the file is covered, not truncated.
        Assert.Equal(100, passages.Count);
        Assert.Equal(1, passages[0].StartLine);
        Assert.Equal(4_000, passages[^1].EndLine);

        var covered = passages.Sum(passage => passage.LineCount);
        Assert.Equal(4_000, covered);
    }

    [Fact]
    public void NeverExceedsEitherCap()
    {
        var text = GeneratedBlock(4_000);

        foreach (var passage in Chunk(text, []))
        {
            Assert.InRange(passage.LineCount, 1, PassageChunker.MaxLines);
            Assert.InRange(passage.Text.Length, 0, PassageChunker.MaxChars);
        }
    }

    [Fact]
    public void TilesTheFileWithoutGapsOrOverlap()
    {
        var passages = Chunk(GeneratedBlock(500), []);

        for (var index = 1; index < passages.Count; index++)
        {
            Assert.Equal(passages[index - 1].EndLine + 1, passages[index].StartLine);
        }
    }

    [Fact]
    public void PassagesHoldExactlyTheLinesTheyClaim()
    {
        var text = GeneratedBlock(300);
        var lines = SourceLines.Split(text);

        foreach (var passage in Chunk(text, []))
        {
            // The passage text is trimmed at the edges, because that is what a TEXT column round-trips.
            var expected = string.Join('\n', lines.Skip(passage.StartLine - 1).Take(passage.LineCount)).Trim();

            Assert.Equal(expected, passage.Text);
        }
    }

    [Fact]
    public void StartsPassagesAtDeclarationsAndNamesThem()
    {
        var text = ThreeMethodsSource();
        var facts = HeuristicSyntaxFactsExtractor.Create().Extract(
            File("src/Geometry/Service.cs", "csharp"),
            text,
            TestContext.Current.CancellationToken);

        var passages = PassageChunker.Chunk(text, facts.Symbols, TestContext.Current.CancellationToken);

        Assert.True(passages.Count >= 3, $"expected one passage per method, got {passages.Count}");

        // Each window is longer than the remaining room for the next method, so the cap lands each split on a
        // declaration instead of mid-statement.
        foreach (var passage in passages.Where(candidate => candidate.SymbolKey is not null))
        {
            // The header that opens the type is its own passage and is named after the type; every other named
            // passage is one of the methods.
            var isType = passage.SymbolKey!.EndsWith("#class", StringComparison.Ordinal);

            Assert.StartsWith("Demo.Service", passage.SymbolKey, StringComparison.Ordinal);
            Assert.EndsWith(isType ? "#class" : "#method", passage.SymbolKey, StringComparison.Ordinal);
            Assert.Equal(isType ? PassageKinds.Type : PassageKinds.Member, passage.Kind);
        }

        Assert.Contains(passages, passage => passage.SymbolKey == "Demo.Service.First#method");
        Assert.Contains(passages, passage => passage.SymbolKey == "Demo.Service.Second#method");
        Assert.Contains(passages, passage => passage.SymbolKey == "Demo.Service.Third#method");
    }

    [Fact]
    public void SplitsLongDeclarationsAtTheCapAndKeepsTheNameOnTheFirstPiece()
    {
        var text = LongMethodSource(90);

        var facts = HeuristicSyntaxFactsExtractor.Create().Extract(
            File("src/Geometry/Long.cs", "csharp"),
            text,
            TestContext.Current.CancellationToken);

        var passages = PassageChunker.Chunk(text, facts.Symbols, TestContext.Current.CancellationToken);
        var named = passages.Where(passage => passage.SymbolKey is not null).ToList();

        // The header opens the type, then the method it declares is named, and the pieces the cap forces after that
        // carry no name because they start mid-body.
        Assert.Equal(2, named.Count);
        Assert.Equal("Demo.Long#class", named[0].SymbolKey);
        Assert.Equal(PassageKinds.Type, named[0].Kind);
        Assert.Equal("Demo.Long.Run#method", named[1].SymbolKey);
        Assert.Equal(PassageKinds.Member, named[1].Kind);
        Assert.Equal(5, named[1].StartLine);
        Assert.InRange(named[1].LineCount, 1, PassageChunker.MaxLines);

        Assert.Equal(99, passages[^1].EndLine);
        Assert.True(passages.Count > 3, "a 99-line method cannot fit in the cap");
    }

    [Fact]
    public void PrefersBlankLinesOverHardSplits()
    {
        // Blocks of five lines with a blank between them: the cap forces splits, and each one lands on a blank line
        // rather than in the middle of a block.
        var text = string.Join(
            "\n",
            Enumerable.Range(0, 120).Select(index => index % 6 == 5 ? string.Empty : $"line {index}"));

        var passages = Chunk(text, []);
        var lines = SourceLines.Split(text);

        Assert.True(passages.Count > 1);

        foreach (var passage in passages.Take(passages.Count - 1))
        {
            Assert.True(
                string.IsNullOrWhiteSpace(lines[passage.EndLine - 1]),
                $"passage {passage.StartLine}-{passage.EndLine} should end on a blank line");
        }
    }

    [Fact]
    public void IgnoresAnEmptyFile()
    {
        Assert.Empty(Chunk(string.Empty, []));
    }

    [Fact]
    public void RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => PassageChunker.Chunk(null!, [], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(
            () => PassageChunker.Chunk("text", null!, TestContext.Current.CancellationToken));
    }

    private static string ThreeMethodsSource()
    {
        var builder = new StringBuilder();
        builder.Append("namespace Demo;\n\npublic sealed class Service\n{\n");

        foreach (var name in new[] { "First", "Second", "Third" })
        {
            builder.Append("    public int ").Append(name).Append("(int seed)\n    {\n");

            for (var index = 0; index < 30; index++)
            {
                builder.Append("        seed = Mix(seed, ").Append(index).Append(");\n");
            }

            builder.Append("        return seed;\n    }\n\n");
        }

        builder.Append("}\n");

        return builder.ToString();
    }

    private static string LongMethodSource(int bodyLines)
    {
        var builder = new StringBuilder();
        builder.Append("namespace Demo;\n\npublic sealed class Long\n{\n    public int Run(int seed)\n    {\n");

        for (var index = 0; index < bodyLines; index++)
        {
            builder.Append("        seed = Mix(seed, ").Append(index).Append(");\n");
        }

        builder.Append("        return seed;\n    }\n}\n");

        return builder.ToString();
    }

    private static string GeneratedBlock(int lineCount)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < lineCount; index++)
        {
            builder.Append("        var value").Append(index).Append(" = Compute(").Append(index).Append(");\n");
        }

        return builder.ToString();
    }

    private static List<Passage> Chunk(string text, IReadOnlyList<ExtractedSymbol> symbols) =>
        PassageChunker.Chunk(text, symbols, TestContext.Current.CancellationToken);

    private static ScannedFile File(string path, string language) => new()
    {
        Path = path,
        FullPath = "C:/workspace/" + path,
        Language = language,
        SizeBytes = 0,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
    };
}
