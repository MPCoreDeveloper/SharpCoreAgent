using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Evaluation;

/// <summary>Runs scenarios sequentially against the production composition root and aggregates their reports.</summary>
internal static class EvaluationRunner
{
    public static async Task<EvaluationReport> RunAllAsync(
        IEnumerable<EvaluationScenario> scenarios,
        CancellationToken cancellationToken)
    {
        var reports = new List<ScenarioReport>();
        foreach (var scenario in scenarios)
        {
            reports.Add(await RunScenarioAsync(scenario, cancellationToken).ConfigureAwait(false));
        }

        return new EvaluationReport
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Scenarios = reports,
            Summary = Summarize(reports),
        };
    }

    public static async Task<ScenarioReport> RunScenarioAsync(
        EvaluationScenario scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        using var fixture = EvaluationWorkspace.Create(scenario);
        var before = fixture.Snapshot();
        var model = new RecordedChatModel(scenario.Turns);
        using var provider = BuildProvider(fixture.Root, model, scenario.Turns.Count);
        var workspace = Workspace.Create(fixture.Root);
        var registry = provider.GetRequiredService<IToolRegistry>();
        var shell = registry.Find("run_shell")
            ?? throw new EvaluationException("the production registry does not contain run_shell");
        var context = new ToolInvocationContext(workspace)
        {
            Model = RecordedChatModel.Descriptor,
            Approval = ApprovalMode.AllowAll,
        };

        if (scenario.Grade.RuntimeProbe is { Length: > 0 } probe)
        {
            var available = await EvaluationGrader.RunCommandAsync(shell, context, "runtime-probe", probe, 30, cancellationToken)
                .ConfigureAwait(false);
            if (!EvaluationGrader.ExitZero(available))
            {
                return Skipped(scenario, probe);
            }
        }

        var timer = Stopwatch.StartNew();
        var options = provider.GetRequiredService<AgentOptions>();
        var session = new AgentSession(workspace, RecordedChatModel.Descriptor, ApprovalMode.AllowAll, options.SystemPrompt)
        {
            Mode = AgentMode.Act,
        };
        var report = await provider.GetRequiredService<CodingAgent>()
            .RunTurnAsync(session, scenario.Prompt, null, cancellationToken)
            .ConfigureAwait(false);

        var changes = fixture.ChangesSince(before);
        var unexpected = EvaluationGrader.UnexpectedChanges(scenario, changes);
        var failures = await EvaluationGrader.CheckTurnAsync(scenario, fixture.Root, report, unexpected, cancellationToken)
            .ConfigureAwait(false);

        fixture.MaterialiseGrading(scenario);
        var grade = await EvaluationGrader.GradeAsync(scenario, shell, context, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        failures.AddRange(grade.Failures);

        return new ScenarioReport
        {
            Id = scenario.Id,
            Language = scenario.Language,
            Status = failures.Count == 0 ? EvaluationStatus.Passed : EvaluationStatus.Failed,
            Outcome = report.Outcome.ToString(),
            IsUnverified = report.IsUnverified,
            Iterations = report.Iterations,
            ToolCalls = report.ToolResults.Count,
            ToolErrors = report.ToolResults.Count(result => result.IsError),
            ToolRefusals = report.ToolResults.Count(result => result.IsRefused),
            ActualChanges = changes,
            UnexpectedChanges = unexpected,
            InputTokens = report.Usage.InputTokens,
            OutputTokens = report.Usage.OutputTokens,
            ElapsedMilliseconds = (long)timer.Elapsed.TotalMilliseconds,
            CommandsRun = report.CommandsRun,
            FinalText = report.FinalText,
            ToolResults = [.. report.ToolResults.Select(result => EvaluationGrader.ReportResult(result, fixture.Root))],
            GradeResult = grade.Result,
            Failures = failures,
        };
    }

    private static ServiceProvider BuildProvider(string root, RecordedChatModel model, int turnCount)
    {
        var services = new ServiceCollection();
        services.AddSharpCoreAgent(new SharpCoreAgentOptions
        {
            Provider = "demo",
            WorkspaceRoot = root,
            IndexEnabled = false,
            Approval = ApprovalMode.AllowAll,
            MaxIterations = Math.Max(16, turnCount + 2),
            MaxOutputTokensPerTurn = 4_000,
            MaxTotalTokens = 500_000,
        });
        services.Replace(ServiceDescriptor.Singleton<IChatModelResolver>(model));
        services.Replace(ServiceDescriptor.Singleton<IToolApprover>(new EvaluationApprover()));
        return services.BuildServiceProvider();
    }

    private static ScenarioReport Skipped(EvaluationScenario scenario, string probe) => new()
    {
        Id = scenario.Id,
        Language = scenario.Language,
        Status = EvaluationStatus.Skipped,
        Outcome = "NotRun",
        Failures = [$"runtime probe '{probe}' did not return exit code 0"],
    };

    private static EvaluationSummary Summarize(List<ScenarioReport> reports)
    {
        var attempted = reports.Where(report => report.Status != EvaluationStatus.Skipped).ToArray();
        var passed = reports.Count(report => report.Status == EvaluationStatus.Passed);
        return new EvaluationSummary
        {
            Total = reports.Count,
            Passed = passed,
            Failed = reports.Count(report => report.Status == EvaluationStatus.Failed),
            Skipped = reports.Count(report => report.Status == EvaluationStatus.Skipped),
            Attempted = attempted.Length,
            SuccessRate = attempted.Length == 0 ? null : passed / (double)attempted.Length,
            UnintendedChanges = reports.Sum(report => report.UnexpectedChanges.Count),
            InputTokens = TotalUsage(attempted, report => report.InputTokens),
            OutputTokens = TotalUsage(attempted, report => report.OutputTokens),
        };
    }

    private static int? TotalUsage(IEnumerable<ScenarioReport> reports, Func<ScenarioReport, int?> select)
    {
        var values = reports.Select(select).ToArray();
        return values.Length == 0 || values.Any(value => !value.HasValue) ? null : values.Sum(value => value!.Value);
    }
}
