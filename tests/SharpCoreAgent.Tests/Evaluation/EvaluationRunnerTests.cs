using SharpCoreAgent.Evaluation;
using Xunit;

namespace SharpCoreAgent.Tests.Evaluation;

public sealed class EvaluationRunnerTests
{
    [Fact]
    public async Task ARecordedTaskPassesThroughTheRealLoopAndIndependentGrade()
    {
        using var fixture = EvaluationTestScenario.Create();

        var report = await EvaluationRunner.RunScenarioAsync(fixture.Value, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationStatus.Passed, report.Status);
        Assert.Equal("Completed", report.Outcome);
        Assert.Equal(4, report.Iterations);
        Assert.Equal(3, report.ToolCalls);
        var change = Assert.Single(report.ActualChanges);
        Assert.Equal("modified", change.Kind);
        Assert.Equal("value.txt", change.Path);
        Assert.Empty(report.UnexpectedChanges);
        Assert.NotNull(report.GradeResult);
        Assert.True(report.InputTokens > 0);
        Assert.True(report.OutputTokens > 0);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task AnIncorrectEditFailsEvenWhenBothCommandsExitZero()
    {
        using var fixture = EvaluationTestScenario.Create();
        var turns = fixture.Value.Turns.ToArray();
        var scenario = fixture.Value with { Turns = turns };
        turns[1] = EvaluationTestScenario.Call(
            "replace_in_file", """{"path":"value.txt","find":"old","replace":"wrong"}""");

        var report = await EvaluationRunner.RunScenarioAsync(scenario, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationStatus.Failed, report.Status);
        Assert.Contains(report.Failures, failure => failure.Contains("does not contain 'new'", StringComparison.Ordinal));
        Assert.Contains("exit code: 0", report.GradeResult!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeOutsideTheDeclaredSetFailsTheTask()
    {
        using var fixture = EvaluationTestScenario.Create();
        var turns = fixture.Value.Turns.ToArray();
        var scenario = fixture.Value with { Turns = turns };
        var edit = turns[1].Calls[0];
        turns[1] = turns[1] with
        {
            Calls =
            [
                edit,
                new RecordedCall
                {
                    Name = "write_file",
                    Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"extra.txt","content":"unintended"}""").RootElement.Clone(),
                },
            ],
        };

        var report = await EvaluationRunner.RunScenarioAsync(scenario, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationStatus.Failed, report.Status);
        Assert.Equal("extra.txt", Assert.Single(report.UnexpectedChanges));
        Assert.Contains("unexpected workspace change: extra.txt", report.Failures);
    }

    [Fact]
    public async Task ARunShellCommandThatExitedNonZeroIsNotVerification()
    {
        using var fixture = EvaluationTestScenario.Create();
        var turns = fixture.Value.Turns.ToArray();
        var scenario = fixture.Value with
        {
            Turns = turns,
            Verification = fixture.Value.Verification with { Command = "exit 1" },
        };
        turns[2] = EvaluationTestScenario.Call("run_shell", """{"command":"exit 1","timeout_seconds":30}""");

        var report = await EvaluationRunner.RunScenarioAsync(scenario, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationStatus.Failed, report.Status);
        Assert.Contains(report.Failures, failure => failure.Contains("no passing run_shell verification", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedIndependentGradeFailsTheTask()
    {
        using var fixture = EvaluationTestScenario.Create();
        var scenario = fixture.Value with { Grade = fixture.Value.Grade with { Command = "exit 1" } };

        var report = await EvaluationRunner.RunScenarioAsync(scenario, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationStatus.Failed, report.Status);
        Assert.Contains("independent grading command did not return exit code 0", report.Failures);
    }

    [Fact]
    public async Task AnAbsentRuntimeSkipsBeforeTheRecordedModelRuns()
    {
        using var fixture = EvaluationTestScenario.Create();
        var scenario = fixture.Value with { Grade = fixture.Value.Grade with { RuntimeProbe = "exit 1" } };

        var report = await EvaluationRunner.RunScenarioAsync(scenario, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationStatus.Skipped, report.Status);
        Assert.Equal("NotRun", report.Outcome);
        Assert.Equal(0, report.Iterations);
        Assert.Empty(report.ActualChanges);
        Assert.Null(report.GradeResult);
        Assert.Contains("runtime probe 'exit 1'", Assert.Single(report.Failures), StringComparison.Ordinal);
    }
}
