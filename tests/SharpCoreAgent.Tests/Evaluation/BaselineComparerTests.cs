using SharpCoreAgent.Evaluation;
using Xunit;

namespace SharpCoreAgent.Tests.Evaluation;

public sealed class BaselineComparerTests
{
    [Fact]
    public void TimingChangesAreIgnoredButSemanticRegressionsAreNamed()
    {
        var baseline = Report();
        var current = Report();
        current = current with
        {
            Scenarios =
            [
                current.Scenarios[0] with
                {
                    Iterations = 5,
                    InputTokens = 101,
                    UnexpectedChanges = ["extra.txt"],
                    ElapsedMilliseconds = 99_999,
                },
            ],
        };

        var regressions = BaselineComparer.Regressions(baseline, current);

        Assert.Equal(3, regressions.Count);
        Assert.Contains(regressions, line => line.Contains("iterations 4 -> 5", StringComparison.Ordinal));
        Assert.Contains(regressions, line => line.Contains("input tokens 100 -> 101", StringComparison.Ordinal));
        Assert.Contains(regressions, line => line.Contains("unexpected changes 0 -> 1", StringComparison.Ordinal));
        Assert.DoesNotContain(regressions, line => line.Contains("99,999", StringComparison.Ordinal));
    }

    [Fact]
    public void APreviouslyPassingTaskCannotBecomeSkippedWithoutARegression()
    {
        var baseline = Report();
        var current = Report();
        current = current with
        {
            Scenarios =
            [
                current.Scenarios[0] with
                {
                    Status = EvaluationStatus.Skipped,
                    Outcome = "NotRun",
                    Iterations = 0,
                    ToolCalls = 0,
                },
            ],
        };

        var regressions = BaselineComparer.Regressions(baseline, current);

        Assert.Contains(regressions, line => line.Contains("status passed -> skipped", StringComparison.Ordinal));
        Assert.Contains(regressions, line => line.Contains("outcome Completed -> NotRun", StringComparison.Ordinal));
    }

    private static EvaluationReport Report() => new()
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        Scenarios =
        [
            new ScenarioReport
            {
                Id = "sample",
                Language = "text",
                Status = EvaluationStatus.Passed,
                Outcome = "Completed",
                Iterations = 4,
                ToolCalls = 3,
                InputTokens = 100,
                OutputTokens = 20,
                ElapsedMilliseconds = 1,
                GradeResult = new EvaluationToolResult { CallId = "grade", Tool = "run_shell", Content = "one" },
            },
        ],
        Summary = new EvaluationSummary { Total = 1, Passed = 1, Attempted = 1, SuccessRate = 1 },
    };
}
