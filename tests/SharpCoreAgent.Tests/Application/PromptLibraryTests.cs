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
    public void TheShippedPromptPointsAtTheBatchAndTheHandle()
    {
        // Two rules the model has to know without reading a schema: fragments can be batched, and a
        // command that outlives its wait is polled rather than re-run.
        Assert.Contains("edits array", PromptLibrary.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("wait_seconds", PromptLibrary.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("shell_output", PromptLibrary.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitPromptIsLeftAlone() =>
        Assert.Equal(
            "Answer in one sentence.",
            new SharpCoreAgentOptions { SystemPrompt = "Answer in one sentence." }.ToAgentOptions().SystemPrompt);

    [Fact]
    public void TheWebRuleShipsOnlyWhenTheNetworkIsOn()
    {
        Assert.Equal(PromptLibrary.SystemPrompt, PromptLibrary.SystemPromptFor(indexAvailable: false, webAvailable: false));
        Assert.EndsWith(PromptLibrary.WebRule, PromptLibrary.SystemPromptFor(false, webAvailable: true), StringComparison.Ordinal);

        var both = PromptLibrary.SystemPromptFor(indexAvailable: true, webAvailable: true);
        Assert.Contains(PromptLibrary.IndexRule, both, StringComparison.Ordinal);
        Assert.True(
            both.IndexOf(PromptLibrary.WebRule, StringComparison.Ordinal) >
            both.IndexOf(PromptLibrary.IndexRule, StringComparison.Ordinal),
            "the web rule belongs after the index rule, because the index is the nearer capability");
    }

    [Fact]
    public void TheShippedPromptPromisesNoNetworkOfItsOwn()
    {
        // Same rule as the retrieval tools: a prompt that names fetch_url in a build without --web teaches the model to
        // call a tool that is not registered.
        Assert.DoesNotContain("fetch_url", PromptLibrary.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("fetch_url", PromptLibrary.WebRule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContainerPromptFollowsTheWebFlag()
    {
        var withWeb = new SharpCoreAgentOptions { WebAccessEnabled = true }.ToAgentOptions();
        var withoutWeb = new SharpCoreAgentOptions().ToAgentOptions();

        Assert.Contains("fetch_url", withWeb.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch_url", withoutWeb.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedPromptSendsBuildsThroughTheVerifier()
    {
        // Rule 5 named run_shell for builds before this round; with the tool registered unconditionally, the
        // instruction and the tool ship together and no host can have one without the other.
        Assert.Contains("dotnet_verify", PromptLibrary.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("build or tests with run_shell", PromptLibrary.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet_verify",
            PromptLibrary.SystemPromptFor(indexAvailable: true, webAvailable: true, mcpAvailable: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerificationNudgeIsTheLoopsSentenceAndNotTheShippedOne()
    {
        // The nudge names the tool and the obligation, and it is not in the prompt: it ships only when a turn has
        // earned it, which is the whole reason it is a message rather than a rule.
        Assert.Contains("dotnet_verify", PromptLibrary.VerifyNudge, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptLibrary.VerifyNudge, PromptLibrary.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebRuleTellsTheModelAPageCannotGrantItAnything()
    {
        // The one rule that turns a fetched page from an instruction into evidence.
        Assert.Contains("evidence, not instruction", PromptLibrary.WebRule, StringComparison.Ordinal);
        Assert.Contains("truncated", PromptLibrary.WebRule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCodeIntelligenceRuleShipsOnlyWithTheToolsItNames()
    {
        // Same contract as the index and the network: the rule and the three tools travel together, in both directions.
        Assert.Equal(PromptLibrary.SystemPrompt, PromptLibrary.SystemPromptFor(indexAvailable: false));
        Assert.EndsWith(
            PromptLibrary.FSharpRule,
            PromptLibrary.SystemPromptFor(indexAvailable: false, fsharpAvailable: true),
            StringComparison.Ordinal);

        Assert.All(
            ["file_diagnostics", "uses_at", "signature_at"],
            name => Assert.True(
                PromptLibrary.FSharpRule.Contains(name, StringComparison.Ordinal),
                $"the code-intelligence rule does not name '{name}'"));

        Assert.DoesNotContain("file_diagnostics", PromptLibrary.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCodeIntelligenceRuleSaysACleanFileIsNotAPassingBuild()
    {
        // The failure mode this rule exists to prevent: a model that reads "no diagnostics" and reports the work done
        // without the build the loop's verification gate is waiting for.
        Assert.Contains("dotnet_verify", PromptLibrary.FSharpRule, StringComparison.Ordinal);
        Assert.Contains("0-based column", PromptLibrary.FSharpRule, StringComparison.Ordinal);
        Assert.Contains("FSH002", PromptLibrary.FSharpRule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedPromptSaysWhenToDelegate()
    {
        // Rule 8 is the only place the model learns the sub-agent exists, so it has to carry the three facts this round
        // depends on: the name it calls, the transcript nobody else sees, and who is allowed to write.
        var rule = Rule(8);

        Assert.Contains("task", rule, StringComparison.Ordinal);
        Assert.Contains("nothing else of this conversation", rule, StringComparison.Ordinal);
        Assert.Contains("its own transcript", rule, StringComparison.Ordinal);
        Assert.Contains("may write only when this session already allows every tool", rule, StringComparison.Ordinal);
        Assert.Contains("dotnet_verify", rule, StringComparison.Ordinal);

        // Unconditional, unlike the index and the network rules: every host gets this rule because every host gets the
        // tool it names, so no flag can remove it.
        Assert.Contains(
            rule,
            PromptLibrary.SystemPromptFor(true, webAvailable: true, mcpAvailable: true, fsharpAvailable: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubAgentIsToldItSeesNothingElse()
    {
        // The child's own prompt has to say it first, or its opening move is a question the developer answered one
        // message earlier — and there is nobody on the other end of it.
        Assert.Contains("not visible to you", PromptLibrary.SubAgentPrompt, StringComparison.Ordinal);
        Assert.Contains("minus delegation itself", PromptLibrary.SubAgentPrompt, StringComparison.Ordinal);
        Assert.Contains("the caller sees it verbatim", PromptLibrary.SubAgentPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubAgentIsSentToTheVerifierItsOwnGateWaitsFor()
    {
        // The child is a CodingAgent with the same gate. A child prompt that sent a .NET edit through run_shell would
        // leave every delegated edit unverified, which is exactly what the parent's report would then say.
        Assert.Contains("dotnet_verify", PromptLibrary.SubAgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("with run_shell", PromptLibrary.SubAgentPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads one numbered operating rule out of the shipped prompt.
    /// </summary>
    /// <param name="number">The rule's number.</param>
    /// <returns>The whole line, including its number.</returns>
    private static string Rule(int number) =>
        PromptLibrary.SystemPrompt
            .Split('\n')
            .Single(line => line.StartsWith($"{number}. ", StringComparison.Ordinal))
            .TrimEnd('\r');
}