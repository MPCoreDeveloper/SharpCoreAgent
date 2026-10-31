using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Tests.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// Writing a key into a credential file: a file a developer maintains by hand, so the write has to keep
/// what is around it and refuse to touch what it cannot read.
/// </summary>
/// <remarks>
/// Each case asserts on the lines that resulted, because a credential file is text somebody reads: a
/// rotation note that survived a paste is the difference between a file the tool owns and one the
/// developer does.
/// </remarks>
public sealed class CredentialFileUpsertTests
{
    [Fact]
    public void ANewFileAndDirectoryAreCreated()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "nested", "keys.env");

        CredentialFile.Upsert(path, "DEEPSEEK_API_KEY", "sk-deepseek");

        Assert.Equal("sk-deepseek", CredentialFile.Read(path)["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void AnExistingLineIsReplacedWhereItStands()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        File.WriteAllLines(path, ["# rotated every 90 days", "DEEPSEEK_API_KEY=old", "XAI_API_KEY=sk-xai"]);

        CredentialFile.Upsert(path, "DEEPSEEK_API_KEY", "sk-new");

        var lines = File.ReadAllLines(path);
        Assert.Equal(["# rotated every 90 days", "DEEPSEEK_API_KEY=sk-new", "XAI_API_KEY=sk-xai"], lines);
        Assert.Equal("sk-new", CredentialFile.Read(path)["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void ANewKeyIsAppendedAfterABlankLine()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        File.WriteAllLines(path, ["# keys", "XAI_API_KEY=sk-xai"]);

        CredentialFile.Upsert(path, "DEEPSEEK_API_KEY", "sk-deepseek");

        var lines = File.ReadAllLines(path);
        Assert.Equal(["# keys", "XAI_API_KEY=sk-xai", string.Empty, "DEEPSEEK_API_KEY=sk-deepseek"], lines);
    }

    [Fact]
    public void AFileThatCannotBeParsedIsLeftAlone()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        string[] broken = ["DEEPSEEK_API_KEY=first", "DEEPSEEK_API_KEY=second"];
        File.WriteAllLines(path, broken);

        // Rewriting a file this grammar cannot read is how a key nobody can retype gets dropped: the write is
        // refused, and the bytes on disk are the ones that were there.
        Assert.Throws<CredentialFileException>(() => CredentialFile.Upsert(path, "XAI_API_KEY", "sk-xai"));
        Assert.Equal(broken, File.ReadAllLines(path));
    }

    [Fact]
    public void AValueWithALineBreakIsRefusedBeforeTheFileSystem()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");

        Assert.Throws<ArgumentException>(() => CredentialFile.Upsert(path, "DEEPSEEK_API_KEY", "sk-one\nsk-two"));
        Assert.Throws<ArgumentException>(() => CredentialFile.Upsert(path, "DEEPSEEK_API_KEY", "  "));
        Assert.Throws<ArgumentException>(() => CredentialFile.Upsert(path, "1KEY", "sk"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheFileIsReadableByItsOwnerOnlyWhereThePlatformSaysSo()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");

        CredentialFile.Upsert(path, "DEEPSEEK_API_KEY", "sk-deepseek");

        if (OperatingSystem.IsWindows())
        {
            // Windows keeps the profile's ACL: the file lives under the user's own configuration directory,
            // and tightening it further needs a package the constitution does not carry.
            Assert.True(File.Exists(path));
            return;
        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
    }
}
