using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The parser is the whole contract a script sees before the agent starts: these tests pin the defaults,
/// the environment fallbacks, and every way a command line can be wrong.
/// </summary>
/// <remarks>
/// Parsing is pure -- no workspace, no container, no credential -- so each test is a few strings in and
/// an assertion out, with no file system and no provider.
/// </remarks>
public sealed class CliOptionsTests
{
    [Fact]
    public void NoArgumentsWithoutATerminalAsksForHelp()
    {
        var options = CliOptions.Parse([], NoEnvironment, NotInteractive);

        Assert.Equal(CliCommand.Help, options.Command);
        Assert.Equal(string.Empty, options.Prompt);
    }

    [Fact]
    public void DefaultsMatchTheContract()
    {
        var options = CliOptions.Parse([], NoEnvironment, NotInteractive);

        Assert.Equal(CliCommand.Help, options.Command);
        Assert.Equal(ModelCatalog.DefaultProvider, options.Provider);
        Assert.Null(options.Model);
        Assert.Equal(Directory.GetCurrentDirectory(), options.Workspace);
        Assert.Equal(ApprovalMode.Ask, options.Approval);
        Assert.Equal(24, options.MaxIterations);
        Assert.Equal(8_000, options.MaxOutputTokens);
        Assert.Equal(250_000, options.MaxTotalTokens);
        Assert.Equal(0.2, options.Temperature);
        Assert.Null(options.BaseUrl);
        Assert.Null(options.SessionPath);
        Assert.True(options.SaveSession);
        Assert.False(options.Json);
        Assert.False(options.Quiet);
        Assert.False(options.Verbose);
        Assert.False(options.ProviderExplicit);
    }

    [Fact]
    public void EnvironmentSuppliesDefaults()
    {
        var options = CliOptions.Parse(["chat"], Environment(
            ("SHARPCOREAGENT_PROVIDER", "GROK"),
            ("SHARPCOREAGENT_MODEL", "grok-4"),
            ("SHARPCOREAGENT_WORKSPACE", Path.GetTempPath())));

        Assert.Equal(CliCommand.Chat, options.Command);
        Assert.Equal("grok", options.Provider);
        Assert.Equal("grok-4", options.Model);
        Assert.Equal(Path.GetTempPath(), options.Workspace);
        Assert.True(options.ProviderExplicit);
    }

    [Fact]
    public void ExplicitArgumentsBeatTheEnvironment()
    {
        var options = CliOptions.Parse(
            ["run", "hi", "--provider", "demo", "--model", "demo-scripted"],
            Environment(("SHARPCOREAGENT_PROVIDER", "grok"), ("SHARPCOREAGENT_MODEL", "grok-4")));

        Assert.Equal("demo", options.Provider);
        Assert.Equal("demo-scripted", options.Model);
    }
    [Fact]
    public void RunJoinsEveryPositionalIntoThePrompt()
    {
        var options = CliOptions.Parse(["run", "list", "the", "files", "in", "src"]);

        Assert.Equal(CliCommand.Run, options.Command);
        Assert.Equal("list the files in src", options.Prompt);
    }

    [Fact]
    public void OptionsMayAppearBeforeOrAfterThePrompt()
    {
        var options = CliOptions.Parse(["--provider", "grok", "run", "hi", "--quiet"]);

        Assert.Equal(CliCommand.Run, options.Command);
        Assert.Equal("grok", options.Provider);
        Assert.Equal("hi", options.Prompt);
        Assert.True(options.Quiet);
    }

    [Fact]
    public void DoubleDashEndsOptionParsing()
    {
        var options = CliOptions.Parse(["run", "--", "--provider", "is", "text"]);

        Assert.Equal(CliCommand.Run, options.Command);
        Assert.Equal("--provider is text", options.Prompt);
        Assert.Equal(ModelCatalog.DefaultProvider, options.Provider);
    }

    [Fact]
    public void RunWithoutAPromptIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run"]));

        Assert.Contains("prompt", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    [Fact]
    public void UnknownOptionIsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hi", "--bogus"]));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["-x"]));
    }

    [Fact]
    public void MissingValueIsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hi", "--model"]));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hi", "--model", "--quiet"]));
    }
    [Fact]
    public void NonNumericBudgetIsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hi", "--max-iterations", "many"]));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hi", "--max-total-tokens", "1.5"]));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hi", "--temperature", "warm"]));
    }

    [Fact]
    public void UnknownProviderNamesTheKnownOnes()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--provider", "openai"]));

        Assert.Contains("openai", error.Message, StringComparison.Ordinal);
        Assert.Contains("deepseek", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderIsNormalisedAndCaseInsensitive()
    {
        var options = CliOptions.Parse(
            ["chat", "--provider", "OpenAI-Compatible", "--base-url", "http://localhost:1234/v1"]);

        Assert.Equal("openai-compatible", options.Provider);
        Assert.Equal("http://localhost:1234/v1", options.BaseUrl);
    }

    [Fact]
    public void JsonIsOnlyValidWithRun()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["chat", "--json"]));

        var options = CliOptions.Parse(["run", "hi", "--json"]);
        Assert.True(options.Json);
    }

    [Fact]
    public void ChatRejectsAPrompt()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["chat", "hi"]));

        Assert.Contains("chat", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    [Fact]
    public void UnknownApprovalModeIsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hi", "--approval", "sometimes"]));
    }
    [Theory]
    [InlineData("readonly", ApprovalMode.ReadOnly)]
    [InlineData("READ-ONLY", ApprovalMode.ReadOnly)]
    [InlineData("ask", ApprovalMode.Ask)]
    [InlineData("allow", ApprovalMode.AllowAll)]
    [InlineData("allow-all", ApprovalMode.AllowAll)]
    public void ApprovalAcceptsTheDocumentedModes(string text, ApprovalMode expected)
    {
        Assert.True(CliOptions.TryParseApproval(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void ApprovalRejectsAnythingElse()
    {
        Assert.False(CliOptions.TryParseApproval("sometimes", out _));
        Assert.False(CliOptions.TryParseApproval(null, out _));
    }

    [Fact]
    public void NoSessionDisablesTheTranscript()
    {
        var options = CliOptions.Parse(["run", "hi", "--no-session"]);

        Assert.False(options.SaveSession);
        Assert.Null(options.SessionPath);
    }

    [Fact]
    public void SessionPathIsKeptAndTurnedBackOn()
    {
        var options = CliOptions.Parse(["run", "hi", "--no-session", "--session", "talk.json"]);

        Assert.True(options.SaveSession);
        Assert.Equal("talk.json", options.SessionPath);
    }

    [Fact]
    public void HelpAndVersionWinAnywhere()
    {
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["run", "hi", "--help"]).Command);
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["chat", "--version"]).Command);
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["help"]).Command);
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["version"]).Command);
    }

    [Fact]
    public void ModelsReportsWhetherTheProviderWasNamed()
    {
        Assert.False(CliOptions.Parse(["models"], NoEnvironment).ProviderExplicit);
        Assert.True(CliOptions.Parse(["models", "--provider", "demo"], NoEnvironment).ProviderExplicit);
    }
    [Fact]
    public void DefaultSessionPathUsesAnInvariantTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpcoreagent-workspace");
        var path = CliOptions.DefaultSessionPath(root, new DateTimeOffset(2026, 10, 23, 10, 15, 2, TimeSpan.Zero));

        Assert.Equal(
            Path.Combine(root, ".sharpcoreagent", "sessions", "20261023T101502Z.json"),
            path);
    }

    [Fact]
    public void TheCommandLineProjectsOntoTheContainerOptions()
    {
        var options = CliOptions.Parse(
        [
            "run", "hi", "--provider", "demo", "--workspace", Path.GetTempPath(),
            "--approval", "allow", "--max-iterations", "3", "--max-output-tokens", "100",
            "--max-total-tokens", "900", "--temperature", "0.5",
        ]);

        var configured = options.ToSharpCoreAgentOptions();

        Assert.Equal("demo", configured.Provider);
        Assert.Equal(Path.GetTempPath(), configured.WorkspaceRoot);
        Assert.Equal(ApprovalMode.AllowAll, configured.Approval);
        Assert.Equal(3, configured.MaxIterations);
        Assert.Equal(100, configured.MaxOutputTokensPerTurn);
        Assert.Equal(900, configured.MaxTotalTokens);
        Assert.Equal(0.5, configured.Temperature);
    }

    [Fact]
    public void FallbackParsesAQualifiedModel()
    {
        var options = CliOptions.Parse(["chat", "--fallback", "grok:grok-4.7"], NoEnvironment);

        Assert.NotNull(options.Fallback);
        Assert.Equal("grok:grok-4.7", options.Fallback.QualifiedName);
        Assert.Equal(options.Fallback, options.ToSharpCoreAgentOptions().Fallback);
        Assert.Null(CliOptions.Parse(["chat"], NoEnvironment).Fallback);
    }

    [Fact]
    public void AnUnknownFallbackIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["chat", "--fallback", "openai:gpt-4o-mini"]));

        Assert.Contains("openai", error.Message, StringComparison.Ordinal);
        Assert.Contains("deepseek", error.Hint!, StringComparison.Ordinal);

        // No provider, and no value at all, are both mistakes the parser can name before a turn starts.
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["chat", "--fallback", "grok-4.7"]));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["chat", "--fallback"]));
    }

    private static string? NoEnvironment(string name) => null;

    private static bool NotInteractive() => false;


    private static Func<string, string?> Environment(params (string Name, string Value)[] pairs) =>
        name => pairs.Where(pair => string.Equals(pair.Name, name, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .FirstOrDefault();
}
