using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Tests.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The grammar of a credential file: small on purpose, and pinned rule by rule because this is the file a
/// developer's key lives in.
/// </summary>
/// <remarks>
/// Every rejection asserts what the message does <em>not</em> contain as well as what it does. A line that
/// failed to parse is a line that may be the key itself, so the message names the line and never repeats
/// its content.
/// </remarks>
public sealed class CredentialFileTests
{
    private const string Source = "keys.env";

    [Fact]
    public void ReadsOneAssignmentPerLine()
    {
        var values = CredentialFile.Parse(["DEEPSEEK_API_KEY=sk-deepseek", "XAI_API_KEY=sk-xai"], Source);

        Assert.Equal(2, values.Count);
        Assert.Equal("sk-deepseek", values["DEEPSEEK_API_KEY"]);
        Assert.Equal("sk-xai", values["XAI_API_KEY"]);
    }

    [Fact]
    public void IgnoresCommentsAndBlankLines()
    {
        var values = CredentialFile.Parse(["", "   ", "# a comment", "   # an indented comment", "KEY=value"], Source);

        Assert.Single(values);
        Assert.Equal("value", values["KEY"]);
    }

    [Fact]
    public void DropsAnExportPrefix()
    {
        var values = CredentialFile.Parse(["export DEEPSEEK_API_KEY=sk-1", "EXPORT XAI_API_KEY=sk-2"], Source);

        Assert.Equal("sk-1", values["DEEPSEEK_API_KEY"]);
        Assert.Equal("sk-2", values["XAI_API_KEY"]);
    }

    [Fact]
    public void TrimsTheSpaceAroundAnAssignment()
    {
        var values = CredentialFile.Parse(["  DEEPSEEK_API_KEY =  sk-deepseek  "], Source);

        Assert.Equal("sk-deepseek", values["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void RemovesMatchingQuotesOnly()
    {
        var values = CredentialFile.Parse(
            ["A=\"double\"", "B='single'", "C=\"mismatched'", "D=\"", "E=two words"],
            Source);

        Assert.Equal("double", values["A"]);
        Assert.Equal("single", values["B"]);
        Assert.Equal("\"mismatched'", values["C"]);
        Assert.Equal("\"", values["D"]);
        Assert.Equal("two words", values["E"]);
    }

    [Fact]
    public void KeepsAHashAndADollarInsideAValue()
    {
        // Nothing is expanded and nothing after a value starts a comment: a key with a # in it is a key.
        var values = CredentialFile.Parse(["A=abc#def", "B=$HOME/keys"], Source);

        Assert.Equal("abc#def", values["A"]);
        Assert.Equal("$HOME/keys", values["B"]);
    }

    [Fact]
    public void KeepsAnEmptyValueSoItCanBeReadAsUnset()
    {
        var values = CredentialFile.Parse(["DEEPSEEK_API_KEY="], Source);

        Assert.Equal(string.Empty, values["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void NamesAreMatchedOrdinally()
    {
        var values = CredentialFile.Parse(["key=lower", "KEY=upper"], Source);

        Assert.Equal(2, values.Count);
        Assert.Equal("lower", values["key"]);
        Assert.Equal("upper", values["KEY"]);
    }

    [Fact]
    public void RejectsALineWithoutAnEqualsWithoutRepeatingIt()
    {
        var error = Assert.Throws<CredentialFileException>(() =>
            CredentialFile.Parse(["DEEPSEEK_API_KEY=sk-1", "DEEPSEEK_API_KEY sk-secret"], Source));

        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("no '='", error.Message, StringComparison.Ordinal);
        Assert.Contains(Source, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsALineWithoutAName()
    {
        var missingName = Assert.Throws<CredentialFileException>(() => CredentialFile.Parse(["=sk-1"], Source));
        var leadingDigit = Assert.Throws<CredentialFileException>(() => CredentialFile.Parse(["1KEY=sk-1"], Source));

        Assert.Contains("line 1", missingName.Message, StringComparison.Ordinal);
        Assert.Contains("variable name", missingName.Message, StringComparison.Ordinal);
        Assert.Contains("line 1", leadingDigit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1", leadingDigit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsADuplicateName()
    {
        var error = Assert.Throws<CredentialFileException>(() =>
            CredentialFile.Parse(["DEEPSEEK_API_KEY=sk-1", "# comment", "DEEPSEEK_API_KEY=sk-2"], Source));

        Assert.Contains("line 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("DEEPSEEK_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsAFileFromDisk()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        File.WriteAllLines(path, ["# rotated 2026-10-27", "DEEPSEEK_API_KEY=sk-deepseek", "XAI_API_KEY='sk-xai'"]);

        var values = CredentialFile.Read(path);

        Assert.Equal("sk-deepseek", values["DEEPSEEK_API_KEY"]);
        Assert.Equal("sk-xai", values["XAI_API_KEY"]);
    }

    [Fact]
    public void AMissingFileIsAnErrorNamingThePath()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "keys.env");

        var error = Assert.Throws<CredentialFileException>(() => CredentialFile.Read(path));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void ABlankPathIsRejectedBeforeTheFileSystem()
    {
        Assert.Throws<ArgumentException>(() => CredentialFile.Read(" "));
    }
}
