using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The plumbing every tool shares: typed argument access, the fixed size limits and path containment.
/// </summary>
/// <remarks>
/// These are the parts a model can attack with text, so each rejection is asserted on its message: the
/// model reads that message, and a vague one costs a wasted turn.
/// </remarks>
public sealed class ArgumentAndLimitsTests
{
    [Fact]
    public void ARequiredStringIsReadAsWritten()
    {
        using var reader = ToolArgumentReader.Parse("read_file", """{"path":"  src/app.cs  "}""");

        Assert.Equal("  src/app.cs  ", reader.GetRequiredString("path"));
        Assert.True(reader.Has("path"));
        Assert.False(reader.Has("missing"));
    }

    [Fact]
    public void AMissingRequiredStringNamesTheParameter()
    {
        using var reader = ToolArgumentReader.Parse("read_file", """{"other":1}""");

        var failure = Assert.Throws<ToolArgumentException>(() => reader.GetRequiredString("path"));

        Assert.Equal("read_file: parameter 'path' is required", failure.Message);
        Assert.Equal("path", failure.Parameter);
    }

    [Fact]
    public void AnEmptyRequiredStringIsRejectedUnlessEmptyIsMeaningful()
    {
        using var reader = ToolArgumentReader.Parse("write_file", """{"content":"   "}""");

        Assert.Throws<ToolArgumentException>(() => reader.GetRequiredString("content"));
        Assert.Equal("   ", reader.GetRequiredString("content", allowEmpty: true));
    }

    [Fact]
    public void AStringOfTheWrongKindIsRejectedWithItsKind()
    {
        using var reader = ToolArgumentReader.Parse("read_file", """{"path":42}""");

        var failure = Assert.Throws<ToolArgumentException>(() => reader.GetRequiredString("path"));

        Assert.Contains("must be a string, not a number", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentOrNullArgumentMeansNoValue()
    {
        using var reader = ToolArgumentReader.Parse("read_file", """{"a":null,"b":"value"}""");

        Assert.Null(reader.GetOptionalString("a"));
        Assert.Null(reader.GetOptionalString("missing"));
        Assert.Equal("value", reader.GetOptionalString("b"));
    }

    [Fact]
    public void OptionalArgumentsFallBackWhenAbsent()
    {
        using var reader = ToolArgumentReader.Parse("list_directory", """{"limit":10,"recursive":true}""");

        Assert.Equal(10, reader.GetOptionalInt("limit", fallback: 200));
        Assert.Equal(200, reader.GetOptionalInt("absent", fallback: 200));
        Assert.True(reader.GetOptionalBool("recursive", fallback: false));
        Assert.False(reader.GetOptionalBool("absent", fallback: false));
    }

    [Fact]
    public void AnIntegerOutsideItsRangeIsRejected()
    {
        using var reader = ToolArgumentReader.Parse("list_directory", """{"limit":5000}""");

        var failure = Assert.Throws<ToolArgumentException>(() => reader.GetOptionalInt("limit", 200, 1, 500));

        Assert.Contains("not 5000", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatIsNotAnIntegerIsRejected()
    {
        using var reader = ToolArgumentReader.Parse("list_directory", """{"limit":"ten"}""");

        Assert.Throws<ToolArgumentException>(() => reader.GetOptionalInt("limit", 200));
    }

    [Fact]
    public void ABooleanOfTheWrongKindIsRejected()
    {
        using var reader = ToolArgumentReader.Parse("write_file", """{"overwrite":"yes"}""");

        var failure = Assert.Throws<ToolArgumentException>(() => reader.GetOptionalBool("overwrite", false));

        Assert.Contains("must be true or false", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankArgumentsAreAnEmptyObject()
    {
        foreach (var arguments in new[] { string.Empty, "   ", null })
        {
            using var reader = ToolArgumentReader.Parse("list_directory", arguments);

            Assert.False(reader.Has("anything"));
            Assert.Null(reader.GetOptionalString("anything"));
        }
    }

    [Fact]
    public void ArgumentsThatAreNotJsonAreRejected()
    {
        var failure = Assert.Throws<ToolArgumentException>(() => ToolArgumentReader.Parse("read_file", "{"));

        Assert.Contains("arguments are not valid JSON", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsThatAreNotAnObjectAreRejected()
    {
        var array = Assert.Throws<ToolArgumentException>(() => ToolArgumentReader.Parse("read_file", "[1,2]"));
        var scalar = Assert.Throws<ToolArgumentException>(() => ToolArgumentReader.Parse("read_file", "\"text\""));

        Assert.Contains("must be a JSON object, not an array", array.Message, StringComparison.Ordinal);
        Assert.Contains("not a string", scalar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankToolNameIsRejected() =>
        Assert.Throws<ArgumentException>(() => ToolArgumentReader.Parse("  ", "{}"));

    [Fact]
    public void BoundingTextKeepsTheHeadAndSaysItShortened()
    {
        var bounded = TextLimits.Bound(new string('a', 50), 20, out var truncated);

        Assert.True(truncated);
        Assert.EndsWith(TextLimits.TruncatedMarker, bounded, StringComparison.Ordinal);
        Assert.True(bounded.Length <= 20, bounded);
    }

    [Fact]
    public void BoundingTextWithNoLimitReturnsItUnchanged()
    {
        Assert.Equal("short", TextLimits.Bound("short", 20, out var truncated));
        Assert.False(truncated);
        Assert.Equal("short", TextLimits.Bound("short", 0, out var unlimited));
        Assert.False(unlimited);
    }

    [Fact]
    public void NullTextIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => TextLimits.Bound(null!, 10, out _));

    [Fact]
    public void ABinaryHeadIsOneWithANulByte()
    {
        Assert.True(TextLimits.LooksBinary([0x50, 0x4B, 0x00, 0x01]));
        Assert.False(TextLimits.LooksBinary("plain text"u8));
    }

    [Fact]
    public void AMissingFileIsNotSearchable()
    {
        Assert.False(TextLimits.IsSearchableText(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gone.txt"), out var reason));
        Assert.Equal("missing", reason);
    }

    [Fact]
    public void ATextFileIsSearchableAndItsHeadIsShort()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sca-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hello");

        try
        {
            Assert.True(TextLimits.IsSearchableText(path, out var reason));
            Assert.Null(reason);
            Assert.Equal("hello"u8.ToArray(), TextLimits.ReadHead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ABinaryFileIsNotSearchable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sca-probe-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02]);

        try
        {
            Assert.False(TextLimits.IsSearchableText(path, out var reason));
            Assert.Equal("binary", reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnOversizedFileIsNotSearchable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sca-probe-{Guid.NewGuid():N}.big");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(TextLimits.MaxSearchableFileBytes + 1);
        }

        try
        {
            Assert.False(TextLimits.IsSearchableText(path, out var reason));
            Assert.Contains("larger than", reason!, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ABlankPathIsRejectedWhenReadingAHead() =>
        Assert.Throws<ArgumentException>(() => TextLimits.ReadHead(" "));
}
