using SharpCoreAgent.Cli;
using SharpCoreAgent.Infrastructure.Credentials;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The credential diagnostic must answer where a key came from without ever printing it.
/// </summary>
public sealed class CredentialsCommandTests
{
    [Fact]
    public void ReportsFileLineEnvironmentAndMissingWithoutValues()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "credentials.env");
        File.WriteAllLines(path, ["# rotation", "DEEPSEEK_API_KEY=file-secret"]);

        var options = CliOptions.Parse(
            ["credentials", "--check", "--credential-file", path],
            _ => null);
        using var output = new StringWriter();

        var exit = CredentialsCommand.Execute(
            options,
            name => name switch
            {
                "XAI_API_KEY" => "environment-secret",
                "OPENAI_API_KEY" => "openai-secret",
                _ => null,
            },
            output);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains($"file:{path}:2", text, StringComparison.Ordinal);
        Assert.Contains("environment", text, StringComparison.Ordinal);
        Assert.Contains("demo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("file-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("environment-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "file-secret",
            CredentialFile.ReadEntries(path).Single().ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderWithNoConfiguredKeyExitsOne()
    {
        var options = CliOptions.Parse(["credentials", "--check", "--provider", "deepseek"], _ => null);
        using var output = new StringWriter();

        var exit = CredentialsCommand.Execute(options, _ => null, output);

        Assert.Equal(1, exit);
        Assert.Contains("missing", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("grok", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckIsRejectedOnEveryOtherCommand()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["chat", "--check"], _ => null));

        Assert.Contains("only valid with the credentials command", error.Message, StringComparison.Ordinal);
    }
}
