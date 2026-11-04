using System.Text.Json;
using SharpCoreAgent.Evaluation;
using Xunit;

namespace SharpCoreAgent.Tests.Evaluation;

public sealed class EvaluationContractTests
{
    [Fact]
    public async Task TheShippedCorpusLoadsInStableOrder()
    {
        var root = EvaluationCli.FindRepository(null);

        var scenarios = await EvaluationScenarioLoader.LoadAllAsync(
            Path.Combine(root, "eng", "evals", "scenarios"),
            selectedId: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["csharp-division", "javascript-discount", "python-cart"], scenarios.Select(scenario => scenario.Id));
        Assert.All(scenarios, scenario => Assert.True(Directory.Exists(scenario.GradingPath) || scenario.Id == "csharp-division"));
    }

    [Fact]
    public async Task TheLoaderRejectsAWorkspaceEscapeBeforeRunningAnything()
    {
        using var fixture = EvaluationTestScenario.Create();
        var scenario = fixture.Value with { AllowedChanges = ["../outside.txt"] };
        var path = Path.Combine(fixture.Root, "scenario.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(scenario, EvaluationJsonContext.Default.EvaluationScenario),
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<EvaluationException>(() =>
            EvaluationScenarioLoader.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("escapes its workspace", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialRunCannotBeComparedWithTheFullBaseline()
    {
        var error = Assert.Throws<EvaluationException>(() =>
            EvaluationArguments.Parse(["--scenario", "sample", "--check"]));

        Assert.Contains("partial run", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCannotOverwriteTheBaselineItReads()
    {
        var root = EvaluationCli.FindRepository(null);
        var baseline = Path.Combine(root, "eng", "evals", "baseline.json");
        var before = await File.ReadAllBytesAsync(baseline, TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync(
            ["--repository", root, "--check", "--baseline", baseline, "--output", baseline],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Contains("cannot write to the recorded baseline", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(baseline, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LiveAndCheckAreMutuallyExclusive()
    {
        var error = Assert.Throws<EvaluationException>(() => EvaluationArguments.Parse(["--live", "--check"]));

        Assert.Contains("deterministic recorded mode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderOptionsRequireLive()
    {
        var error = Assert.Throws<EvaluationException>(() =>
            EvaluationArguments.Parse(["--provider", "grok"]));

        Assert.Contains("require --live", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveModeAcceptsProviderAndKeepsItsLimits()
    {
        var options = EvaluationArguments.Parse(
            ["--live", "--provider", "grok", "--model", "grok-4.7", "--max-total-tokens", "12000"]);

        Assert.True(options.Live);
        Assert.Equal("grok", options.Provider);
        Assert.Equal("grok-4.7", options.Model);
        Assert.Equal(12_000, options.MaxTotalTokens);
    }

    [Fact]
    public void OpenAiCompatibleLiveModeRequiresABaseUrl()
    {
        var error = Assert.Throws<EvaluationException>(() =>
            EvaluationArguments.Parse(["--live", "--provider", "openai-compatible"]));

        Assert.Contains("--base-url", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpMentionsTheLiveModeAndItsLimits()
    {
        Assert.Contains("--live", EvaluationArguments.HelpText, StringComparison.Ordinal);
        Assert.Contains("--max-total-tokens", EvaluationArguments.HelpText, StringComparison.Ordinal);
        Assert.Contains("--credential-file", EvaluationArguments.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpNeedsNeitherARepositoryNorCredentials()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync(["--help"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("coding-task evaluation", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }
}
