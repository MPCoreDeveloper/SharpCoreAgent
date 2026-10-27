using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The two new switches are part of the command-line contract, so their defaults, spellings and failure
/// messages are pinned here rather than discovered in a turn that refused to start.
/// </summary>
public sealed class ColorAndModeOptionTests
{
    [Fact]
    public void DefaultsAreAutoColourAndActMode()
    {
        var options = CliOptions.Parse(["chat"], NoEnvironment);

        Assert.Equal(ColorChoice.Auto, options.Color);
        Assert.Equal(AgentMode.Act, options.Mode);
    }

    [Fact]
    public void ColorAcceptsTheThreeDocumentedWords()
    {
        Assert.Equal(ColorChoice.Auto, CliOptions.Parse(["run", "hello", "--color", "auto"], NoEnvironment).Color);
        Assert.Equal(ColorChoice.Always, CliOptions.Parse(["run", "hello", "--color", "always"], NoEnvironment).Color);
        Assert.Equal(ColorChoice.Never, CliOptions.Parse(["run", "hello", "--color", "never"], NoEnvironment).Color);
    }

    [Fact]
    public void ModePlanIsParsedAndKeepsThePrompt()
    {
        var options = CliOptions.Parse(["run", "plan the change", "--mode", "plan"], NoEnvironment);

        Assert.Equal(AgentMode.Plan, options.Mode);
        Assert.Equal("plan the change", options.Prompt);
    }

    [Fact]
    public void UnknownColourIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["chat", "--color", "rainbow"], NoEnvironment));

        Assert.Contains("auto, always or never", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownModeIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["chat", "--mode", "dream"], NoEnvironment));

        Assert.Contains("plan or act", error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ds", "deepseek", "deepseek-chat")]
    [InlineData("r1", "deepseek", "deepseek-reasoner")]
    [InlineData("reasoner", "deepseek", "deepseek-reasoner")]
    [InlineData("grok", "grok", "grok-4")]
    [InlineData("g3", "grok", "grok-3")]
    public void AliasesResolveToAProviderAndModel(string alias, string provider, string modelId)
    {
        Assert.True(ModelCatalog.TryResolveAlias(alias, out var resolvedProvider, out var resolvedModel));
        Assert.Equal(provider, resolvedProvider);
        Assert.Equal(modelId, resolvedModel);
    }

    [Fact]
    public void AnUnknownAliasIsNotResolved()
    {
        Assert.False(ModelCatalog.TryResolveAlias("gpt", out _, out _));
        Assert.False(ModelCatalog.TryResolveAlias(null, out _, out _));
    }

    private static string? NoEnvironment(string name) => null;
}
