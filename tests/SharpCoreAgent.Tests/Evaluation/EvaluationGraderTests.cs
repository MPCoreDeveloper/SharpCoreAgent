using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Evaluation;
using Xunit;

namespace SharpCoreAgent.Tests.Evaluation;

public sealed class EvaluationGraderTests
{
    [Fact]
    public async Task LiveGradingAcceptsAnEquivalentImplementation()
    {
        using var fixture = EvaluationTestScenario.Create();
        var workspace = Path.Combine(fixture.Root, "workspace");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "value.txt"),
            "equivalent\n",
            TestContext.Current.CancellationToken);
        var report = new AgentReport
        {
            Outcome = AgentOutcome.Completed,
            ModelId = "demo",
            ToolResults =
            [
                new ToolResult
                {
                    CallId = "verify",
                    ToolName = "run_shell",
                    Content = "exit code: 0",
                    Effects = [new ToolEffect(ToolEffectKind.CommandRun, "dotnet --version")],
                },
            ],
        };

        var live = await EvaluationGrader.CheckTurnAsync(
            fixture.Value,
            workspace,
            report,
            [],
            TestContext.Current.CancellationToken,
            requireSourceFragments: false);

        var recorded = await EvaluationGrader.CheckTurnAsync(
            fixture.Value,
            workspace,
            report,
            [],
            TestContext.Current.CancellationToken,
            requireSourceFragments: true);

        Assert.Empty(live);
        Assert.Contains(recorded, failure => failure.Contains("does not contain 'new'", StringComparison.Ordinal));
    }
}
