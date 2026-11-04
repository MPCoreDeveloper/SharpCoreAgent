using System.Text.Json;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Evaluation;

/// <summary>Checks outcomes independently of the model's final prose and of the harness's recorded script.</summary>
internal static class EvaluationGrader
{
    private const int MaxReportedContent = 8_000;

    public static IReadOnlyList<string> UnexpectedChanges(
        EvaluationScenario scenario,
        IReadOnlyList<WorkspaceChange> changes)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var allowed = scenario.AllowedChanges.Select(path => path.Replace('\\', '/')).ToHashSet(comparison);
        return [.. changes.Select(change => change.Path).Where(path => !allowed.Contains(path))];
    }

    public static async Task<List<string>> CheckTurnAsync(
        EvaluationScenario scenario,
        string root,
        AgentReport report,
        IReadOnlyList<string> unexpected,
        CancellationToken cancellationToken,
        bool requireSourceFragments = true)
    {
        var failures = new List<string>();
        if (report.Outcome != AgentOutcome.Completed)
        {
            failures.Add($"agent outcome was {report.Outcome}: {report.Detail ?? "no detail"}");
        }

        if (report.IsUnverified)
        {
            failures.Add("the production turn completed unverified");
        }

        failures.AddRange(unexpected.Select(path => $"unexpected workspace change: {path}"));
        foreach (var expected in scenario.Grade.ExpectedFiles)
        {
            var path = Path.Combine(root, expected.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                failures.Add($"expected file is missing: {expected.Path}");
                continue;
            }

            // A recorded scenario pins the exact edit it was authored with. A live model may choose a
            // different but equivalent implementation, so live mode checks that the file exists and lets
            // the independent grading command decide whether the behaviour is right.
            if (!requireSourceFragments)
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            failures.AddRange(expected.Contains
                .Where(fragment => !content.Contains(fragment, StringComparison.Ordinal))
                .Select(fragment => $"expected file '{expected.Path}' does not contain '{fragment}'"));
        }

        if (!HasPassingVerification(scenario.Verification, report))
        {
            failures.Add($"no passing {scenario.Verification.Tool} verification followed the last file edit");
        }

        return failures;
    }

    public static async Task<(EvaluationToolResult Result, IReadOnlyList<string> Failures)> GradeAsync(
        EvaluationScenario scenario,
        ITool shell,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            shell,
            context,
            "independent-grade",
            scenario.Grade.Command,
            scenario.Grade.TimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        var failures = new List<string>();
        if (result.IsError || result.IsRefused || !ExitZero(result))
        {
            failures.Add("independent grading command did not return exit code 0");
        }

        failures.AddRange(scenario.Grade.ExpectedOutput
            .Where(fragment => !result.Content.Contains(fragment, StringComparison.Ordinal))
            .Select(fragment => $"independent grading output does not contain '{fragment}'"));
        return (ReportResult(result, context.Workspace.Root), failures);
    }

    public static async ValueTask<ToolResult> RunCommandAsync(
        ITool shell,
        ToolInvocationContext context,
        string id,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var arguments = JsonSerializer.Serialize(new { command, timeout_seconds = timeoutSeconds });
        return await shell.ExecuteAsync(new ToolCall(id, "run_shell", arguments), context, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool ExitZero(ToolResult result) =>
        result.Content.Split('\n').Any(line => line.TrimEnd('\r') == "exit code: 0");

    public static EvaluationToolResult ReportResult(ToolResult result, string root)
    {
        var content = result.Content.Replace(root, "<workspace>", StringComparison.OrdinalIgnoreCase);
        var truncated = result.IsTruncated || content.Length > MaxReportedContent;
        if (content.Length > MaxReportedContent)
        {
            content = content[..MaxReportedContent] + "\n[truncated by evaluation report]";
        }

        return new EvaluationToolResult
        {
            CallId = result.CallId,
            Tool = result.ToolName,
            Content = content,
            IsError = result.IsError,
            IsRefused = result.IsRefused,
            IsTruncated = truncated,
        };
    }

    private static bool HasPassingVerification(ScenarioVerification verification, AgentReport report)
    {
        var lastEdit = -1;
        for (var index = 0; index < report.ToolResults.Count; index++)
        {
            if (report.ToolResults[index].Effects.Any(effect =>
                effect.Kind is ToolEffectKind.Created or ToolEffectKind.Modified or ToolEffectKind.Deleted))
            {
                lastEdit = index;
            }
        }

        return report.ToolResults.Skip(lastEdit + 1).Any(result =>
            result.ToolName == verification.Tool
            && !result.IsError
            && !result.IsRefused
            && (verification.Command is null || result.Effects.Any(effect =>
                effect.Kind == ToolEffectKind.CommandRun && effect.Target == verification.Command))
            && (verification.Tool != "run_shell" || ExitZero(result)));
    }
}
