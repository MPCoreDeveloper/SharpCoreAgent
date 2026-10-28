using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Tests.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The resolution order a credential file joins: configuration first, the environment second, a blank
/// value counting as unset in both.
/// </summary>
/// <remarks>
/// The environment reader is injected, so no test mutates the process environment and no test can pass
/// because of a key that happens to be set on the machine running the build. <see cref="EnvironmentCredentialStore.FromFile"/>
/// is the one entry point that reads a real file, and it is exercised against a temporary one.
/// </remarks>
public sealed class CredentialStoreTests
{
    private const string Key = "SHARPCOREAGENT_TEST_API_KEY";

    [Fact]
    public void ReadsAValueFromTheEnvironment()
    {
        var store = new EnvironmentCredentialStore(readEnvironment: name => name == Key ? "from-the-environment" : null);

        Assert.Equal("from-the-environment", store.Find(Key));
        Assert.Equal("from-the-environment", store.GetRequired(Key));
    }

    [Fact]
    public void ConfigurationWinsOverTheEnvironment()
    {
        var store = new EnvironmentCredentialStore(
            new Dictionary<string, string>(StringComparer.Ordinal) { [Key] = "from-the-file" },
            _ => "from-the-environment");

        Assert.Equal("from-the-file", store.Find(Key));
    }

    [Fact]
    public void ABlankWhereTheFileWouldBeFallsThroughToTheEnvironment()
    {
        var store = new EnvironmentCredentialStore(
            new Dictionary<string, string>(StringComparer.Ordinal) { [Key] = string.Empty },
            _ => "from-the-environment");

        Assert.Equal("from-the-environment", store.Find(Key));
    }

    [Fact]
    public void ABlankVariableCountsAsUnset()
    {
        var store = new EnvironmentCredentialStore(readEnvironment: _ => "   ");

        Assert.Null(store.Find(Key));
    }

    [Fact]
    public void AMissingKeyIsNamedWithoutAValue()
    {
        var store = new EnvironmentCredentialStore(readEnvironment: _ => null);

        var error = Assert.Throws<ModelCredentialException>(() => store.GetRequired(Key));

        Assert.Contains(Key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFileReadsTheFileAndWinsOverTheEnvironment()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        File.WriteAllLines(path, [$"{Key}=from-the-file"]);

        var store = EnvironmentCredentialStore.FromFile(path);

        Assert.Equal("from-the-file", store.Find(Key));
    }

    [Fact]
    public void FromFileWithoutAPathIsTheEnvironmentAlone()
    {
        // No file, no overrides: a name nothing defines is not configured, and nothing is read from disk.
        Assert.Null(EnvironmentCredentialStore.FromFile(null).Find(Key));
        Assert.Null(EnvironmentCredentialStore.FromFile("  ").Find(Key));
    }

    [Fact]
    public void AMalformedFileFailsWhenTheStoreIsBuilt()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        File.WriteAllLines(path, [$"{Key} no-equals-here"]);

        var error = Assert.Throws<CredentialFileException>(() => EnvironmentCredentialStore.FromFile(path));

        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no-equals-here", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContainerReadsTheFileItIsGiven()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        File.WriteAllLines(path, [$"{Key}=from-the-file"]);

        using var provider = new ServiceCollection()
            .AddSharpCoreAgent(new SharpCoreAgentOptions { CredentialFilePath = path })
            .BuildServiceProvider();

        Assert.Equal("from-the-file", provider.GetRequiredService<ICredentialStore>().Find(Key));
    }
}
