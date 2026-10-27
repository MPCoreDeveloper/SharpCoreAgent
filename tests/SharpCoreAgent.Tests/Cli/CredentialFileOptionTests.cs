using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The <c>--credential-file</c> option: where the path comes from, and that it reaches the container.
/// </summary>
/// <remarks>
/// The file is never opened here. Parsing keeps a path — the command that needs a key is the one that
/// reads it — so these tests need neither a file system nor a credential.
/// </remarks>
public sealed class CredentialFileOptionTests
{
    [Fact]
    public void TheOptionNamesTheFile()
    {
        var options = CliOptions.Parse(["models", "--credential-file", "keys.env"], NoEnvironment);

        Assert.Equal("keys.env", options.CredentialFile);
        Assert.Equal("keys.env", options.ToSharpCoreAgentOptions().CredentialFilePath);
    }

    [Fact]
    public void NoFileLeavesTheEnvironmentAlone()
    {
        var options = CliOptions.Parse(["models"], NoEnvironment);

        Assert.Null(options.CredentialFile);
        Assert.Null(options.ToSharpCoreAgentOptions().CredentialFilePath);
    }

    [Fact]
    public void TheEnvironmentSuppliesTheDefault()
    {
        var options = CliOptions.Parse(
            ["models"],
            Environment(("SHARPCOREAGENT_CREDENTIAL_FILE", Path.Combine("D:", "keys", ".env"))));

        Assert.Equal(Path.Combine("D:", "keys", ".env"), options.CredentialFile);
    }

    [Fact]
    public void TheArgumentBeatsTheEnvironment()
    {
        var options = CliOptions.Parse(
            ["models", "--credential-file", "argument.env"],
            Environment(("SHARPCOREAGENT_CREDENTIAL_FILE", "environment.env")));

        Assert.Equal("argument.env", options.CredentialFile);
    }

    [Fact]
    public void AMissingValueIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["models", "--credential-file"], NoEnvironment));

        Assert.Contains("--credential-file", error.Message, StringComparison.Ordinal);
        Assert.Contains("needs a value", error.Message, StringComparison.Ordinal);
    }

    private static string? NoEnvironment(string name) => null;

    private static Func<string, string?> Environment(params (string Name, string Value)[] pairs) =>
        name => pairs.Where(pair => string.Equals(pair.Name, name, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .FirstOrDefault();
}
