using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// The prompt is product surface: it tells the model which tools exist, so it has to agree with what the container
/// registers.
/// </summary>
/// <remarks>
/// The rule is asserted as five names plus the citation form rather than as a whole-string snapshot. A snapshot would
/// fail on every rewording, which teaches people to paste the new text without reading it; the five names and the
/// <c>path:start-end</c> obligation are the parts a model's behaviour actually depends on.
/// </remarks>
public sealed class PromptLibraryTests
{
    private static readonly string[] RetrievalTools =
        ["search_code", "find_symbol", "find_references", "code_context", "index_status"];

    [Fact]
    public void TheIndexRuleNamesEveryRetrievalTool() =>
        Assert.All(
            RetrievalTools,
            name => Assert.True(
                PromptLibrary.IndexRule.Contains(name, StringComparison.Ordinal),
                $"the index rule does not name '{name}'"));

    [Fact]
    public void TheIndexRuleCarriesTheCitationForm()
    {
        Assert.Contains("path:start-end", PromptLibrary.IndexRule, StringComparison.Ordinal);
        Assert.Contains("src/Geometry/Circle.cs:12-18", PromptLibrary.IndexRule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndexRuleSaysWhatToDoWhenTheIndexIsMissing() =>
        Assert.Contains("index is missing", PromptLibrary.IndexRule, StringComparison.Ordinal);

    [Fact]
    public void TheShippedPromptPromisesNoToolTheHostMayNotHave() =>
        Assert.All(
            RetrievalTools,
            name => Assert.False(
                PromptLibrary.SystemPrompt.Contains(name, StringComparison.Ordinal),
                $"the shipped prompt names '{name}' without the index rule that explains it"));

    [Fact]
    public void TheRuleIsAppendedOnlyWhenAnIndexExists()
    {
        var without = PromptLibrary.SystemPromptFor(indexAvailable: false);
        var with = PromptLibrary.SystemPromptFor(indexAvailable: true);

        Assert.Equal(PromptLibrary.SystemPrompt, without);
        Assert.StartsWith(PromptLibrary.SystemPrompt, with, StringComparison.Ordinal);
        Assert.EndsWith(PromptLibrary.IndexRule, with, StringComparison.Ordinal);
        Assert.Contains("\n\n", with, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContainerPromptFollowsTheIndexFlag()
    {
        // The wiring the CLI relies on: --no-index must remove the rule, not just the tools.
        var indexed = new SharpCoreAgentOptions().ToAgentOptions();
        var disabled = new SharpCoreAgentOptions { IndexEnabled = false }.ToAgentOptions();

        Assert.Contains("search_code", indexed.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(PromptLibrary.SystemPrompt, disabled.SystemPrompt);
    }

    [Fact]
    public void AnExplicitPromptIsLeftAlone() =>
        Assert.Equal(
            "Answer in one sentence.",
            new SharpCoreAgentOptions { SystemPrompt = "Answer in one sentence." }.ToAgentOptions().SystemPrompt);
}