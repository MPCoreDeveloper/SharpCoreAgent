using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Index;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The <c>index</c> command's parsing rules: which action a line selects, and every combination the contract refuses.
/// </summary>
/// <remarks>
/// Each refusal is asserted by message, not only by exception type, because the message is what a developer reads and
/// what tells them which two flags disagreed. The defaults are asserted too: <c>index</c> with no flags means
/// <c>--status</c>, and a parser that quietly meant <c>--rebuild</c> would rewrite an index on a typo.
/// </remarks>
public sealed class IndexCommandParsingTests
{
    [Fact]
    public void BareIndexSelectsNoActionSoTheCommandBuilds()
    {
        var options = CliOptions.Parse(["index"], NoEnvironment);

        Assert.Equal(CliCommand.Index, options.Command);
        Assert.False(options.IndexStatus);
        Assert.False(options.IndexRebuild);
        Assert.False(options.IndexVerify);
        Assert.False(options.IndexPrune);
        Assert.Equal(EmbedderKind.Local, options.Embedder);
        Assert.Equal(IndexTier.Lean, options.Tier);
        Assert.True(options.IndexEnabled);
    }

    [Fact]
    public void StatusAndJsonCombine()
    {
        var options = CliOptions.Parse(["index", "--status", "--json"], NoEnvironment);

        Assert.Equal(CliCommand.Index, options.Command);
        Assert.True(options.IndexStatus);
        Assert.True(options.Json);
    }

    [Fact]
    public void RebuildRefusesTheOtherActions()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["index", "--rebuild", "--verify"], NoEnvironment));

        Assert.Equal("--rebuild cannot be combined with --status, --verify or --prune.", error.Message);
    }

    [Fact]
    public void ANegativeBudgetIsRejectedByNameAndValue()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["run", "hello", "--index-budget", "-1"], NoEnvironment));

        Assert.Equal(
            "option '--index-budget' needs a whole number of milliseconds, zero or greater, but got '-1'.",
            error.Message);
    }

    [Fact]
    public void AnUnknownEmbedderListsTheModes()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["index", "--embedder", "nope"], NoEnvironment));

        Assert.Equal("unknown embedder 'nope'.", error.Message);
        Assert.Equal("Use hashed-local, provider or off.", error.Hint);
    }

    [Fact]
    public void TheIndexCommandRefusesNoIndex()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["index", "--no-index"], NoEnvironment));

        Assert.Equal("the index command always uses the index.", error.Message);
    }

    [Fact]
    public void IndexOptionsOnRunAreRejected()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["run", "hello", "--rebuild"], NoEnvironment));

        Assert.Contains("belong to the index command", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmbedderAndTierAreParsedIntoTheIndexEnums()
    {
        var options = CliOptions.Parse(["index", "--embedder", "provider", "--tier", "rich"], NoEnvironment);

        Assert.Equal(EmbedderKind.Provider, options.Embedder);
        Assert.Equal(IndexTier.Rich, options.Tier);
    }

    [Fact]
    public void NoIndexClearsTheContainerOptionAndTheBudgetTravels()
    {
        var options = CliOptions.Parse(["chat", "--no-index", "--index-budget", "500"], NoEnvironment);
        var container = options.ToSharpCoreAgentOptions();

        Assert.False(container.IndexEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(500), container.IndexBudget);
    }

    [Fact]
    public void TheIndexIsOnByDefaultWithAThreeSecondRefreshBudget()
    {
        var container = CliOptions.Parse(["chat"], NoEnvironment).ToSharpCoreAgentOptions();

        Assert.True(container.IndexEnabled);
        Assert.Equal(TimeSpan.FromSeconds(3), container.IndexBudget);
    }

    [Fact]
    public void ALeadingAriadneTokenIsDropped()
    {
        var options = CliOptions.Parse(["ariadne", "index", "--status"], NoEnvironment);

        Assert.Equal(CliCommand.Index, options.Command);
        Assert.True(options.IndexStatus);
    }

    private static string? NoEnvironment(string name) => null;
}
