using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Configuration;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The interactive key prompt: what it asks, where it writes, and the one thing it must never do — print
/// the key.
/// </summary>
/// <remarks>
/// The masked reader, the environment and the file are all seams, so a test scripts the conversation a
/// developer would have and then reads the file that resulted. Nothing here touches the process environment
/// or a real terminal.
/// </remarks>
public sealed class CredentialPromptTests
{
    private const string Key = "sk-pasted-secret";

    [Fact]
    public void APastedKeyIsWrittenToTheFileAndNeverPrinted()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        File.WriteAllLines(path, ["# rotated 2026-10-30", "XAI_API_KEY=sk-xai"]);

        var scenario = Scenario(new StringReader("y\n"), path);

        Assert.True(scenario.Prompt.TryFix([DeepSeek()], scenario.Credentials));

        // The key reached the store this process reads, without a restart.
        Assert.Equal(Key, scenario.Credentials.Find("DEEPSEEK_API_KEY"));
        Assert.Equal(Key, scenario.Environment["DEEPSEEK_API_KEY"]);

        // The file kept what was there and gained one line.
        var lines = File.ReadAllLines(path);
        Assert.Contains("# rotated 2026-10-30", lines);
        Assert.Contains("XAI_API_KEY=sk-xai", lines);
        Assert.Contains($"DEEPSEEK_API_KEY={Key}", lines);

        // The one rule: never echoed, not in the question and not in the confirmation.
        Assert.DoesNotContain(Key, scenario.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, scenario.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("saved DEEPSEEK_API_KEY", scenario.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFileIsAdoptedByTheSettingsDocument()
    {
        using var directory = TempDirectory.Create();
        var settings = Path.Combine(directory.Path, "config.json");
        var scenario = Scenario(new StringReader("\n"), credentialPath: null, settingsPath: settings);

        Assert.True(scenario.Prompt.TryFix([DeepSeek()], scenario.Credentials));

        // Written beside the settings file rather than in a workspace, and then named in the document so the
        // next run reads it without being told.
        var expected = Path.Combine(directory.Path, "credentials.env");
        Assert.Equal(expected, AgentSettingsFile.Read(settings)["credential-file"]);
        Assert.Equal(Key, CredentialFile.Read(expected)["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void SayingNoWritesNothingAndLeavesTheKeyMissing()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        var scenario = Scenario(new StringReader("n\n"), path);

        Assert.False(scenario.Prompt.TryFix([DeepSeek()], scenario.Credentials));
        Assert.False(File.Exists(path));
        Assert.Null(scenario.Credentials.Find("DEEPSEEK_API_KEY"));
    }

    [Fact]
    public void WithoutATerminalNothingIsAskedAndNothingIsWritten()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        var scenario = Scenario(new StringReader("y\n"), path, interactive: false);

        Assert.False(scenario.Prompt.TryFix([DeepSeek()], scenario.Credentials));
        Assert.False(File.Exists(path));
        Assert.Empty(scenario.Output.ToString());
    }

    [Fact]
    public void AProviderThatNeedsNoKeyIsNeverAsked()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        var scenario = Scenario(
            new StringReader(string.Empty),
            path,
            readSecret: () => throw new InvalidOperationException("the demo provider needs no key, so nothing may be read"));

        Assert.True(scenario.Prompt.TryFix([ModelCatalog.DescriptorFor("demo", null)], scenario.Credentials));
        Assert.Empty(scenario.Output.ToString());
    }

    [Fact]
    public void EachProviderIsAskedOnceEvenWhenTwoRolesNameIt()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        var secrets = new Queue<string>(["sk-deepseek", "sk-xai"]);
        var scenario = Scenario(new StringReader("y\ny\n"), path, readSecret: secrets.Dequeue);

        var models = new List<ModelDescriptor>
        {
            DeepSeek(),
            ModelCatalog.DescriptorFor("grok", "grok-4.7"),
            DeepSeek(),
        };

        Assert.True(scenario.Prompt.TryFix(models, scenario.Credentials));

        // One question per provider: the second DeepSeek entry is the same key, not a second paste. The queue
        // is empty precisely because only two secrets were read.
        Assert.Empty(secrets);
        Assert.Equal("sk-deepseek", scenario.Credentials.Find("DEEPSEEK_API_KEY"));
        Assert.Equal("sk-xai", scenario.Credentials.Find("XAI_API_KEY"));
    }

    [Fact]
    public void ThePreflightTakesAPastedKeyAndThenReportsNothingMissing()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        var scenario = Scenario(new StringReader("y\n"), path);
        using var reported = new StringWriter();

        Assert.True(CliHost.TryEnsureCredentials([DeepSeek()], scenario.Credentials, reported, prompt: scenario.Prompt));
        Assert.Empty(reported.ToString());
    }

    [Fact]
    public void WithoutAPromptThePreflightPrintsTheThreeRoutes()
    {
        var credentials = new EnvironmentCredentialStore(readEnvironment: _ => null);
        using var error = new StringWriter();

        Assert.False(CliHost.TryEnsureCredentials([DeepSeek()], credentials, error));

        Assert.Contains("DEEPSEEK_API_KEY", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--credential-file", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("config set credential-file", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPasteWritesNothing()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        var scenario = Scenario(new StringReader("y\n"), path, readSecret: () => "   ");

        Assert.False(scenario.Prompt.TryFix([DeepSeek()], scenario.Credentials));
        Assert.Contains("nothing was pasted", scenario.Error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    private static ModelDescriptor DeepSeek() => ModelCatalog.DescriptorFor("deepseek", null);

    /// <summary>
    /// Builds a prompt whose terminal, environment and file are all test doubles.
    /// </summary>
    /// <param name="input">The answers a developer would type.</param>
    /// <param name="credentialPath">The file to write, or null to fall back to the default beside the settings.</param>
    /// <param name="settingsPath">The settings file, when one is wanted.</param>
    /// <param name="readSecret">What the masked reader returns; the pasted key by default.</param>
    /// <param name="interactive">Whether the terminal probe says a keyboard is attached.</param>
    /// <returns>The prompt, the store it must satisfy, the environment it writes, and the two streams.</returns>
    private static PromptScenario Scenario(
        StringReader input,
        string? credentialPath,
        string? settingsPath = null,
        Func<string?>? readSecret = null,
        bool interactive = true)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (settingsPath is not null)
        {
            // The default credential file lives beside the settings document, which is how it is found.
            environment["SHARPCOREAGENT_CONFIG"] = settingsPath;
        }

        var credentials = new EnvironmentCredentialStore(readEnvironment: name => environment.GetValueOrDefault(name));
        var output = new StringWriter();
        var error = new StringWriter();
        var prompt = new CredentialPrompt(
            input,
            output,
            error,
            () => interactive,
            credentialPath,
            settingsPath,
            readSecret ?? (() => Key),
            name => environment.GetValueOrDefault(name),
            (name, value) => environment[name] = value);

        return new PromptScenario(prompt, credentials, environment, output, error);
    }

    /// <summary>One scripted prompt and everything it touched.</summary>
    /// <param name="Prompt">The prompt under test.</param>
    /// <param name="Credentials">The store a paste has to satisfy.</param>
    /// <param name="Environment">The environment a paste is written to.</param>
    /// <param name="Output">What the developer would read.</param>
    /// <param name="Error">What a refusal would print.</param>
    private sealed record PromptScenario(
        CredentialPrompt Prompt,
        EnvironmentCredentialStore Credentials,
        Dictionary<string, string?> Environment,
        StringWriter Output,
        StringWriter Error);
}
