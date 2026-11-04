namespace SharpCoreAgent.Evaluation;

/// <summary>Compares durable quality signals while ignoring run-specific timing and output text.</summary>
internal static class BaselineComparer
{
    public static IReadOnlyList<string> Regressions(EvaluationReport baseline, EvaluationReport current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var regressions = new List<string>();
        var before = baseline.Scenarios.ToDictionary(scenario => scenario.Id, StringComparer.Ordinal);
        var after = current.Scenarios.ToDictionary(scenario => scenario.Id, StringComparer.Ordinal);

        foreach (var id in before.Keys.Except(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            regressions.Add($"{id}: scenario is missing from the current run");
        }

        foreach (var id in after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            regressions.Add($"{id}: scenario was not present in the baseline");
        }

        foreach (var id in before.Keys.Intersect(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Compare(before[id], after[id], regressions);
        }

        return regressions;
    }

    private static void Compare(ScenarioReport before, ScenarioReport after, List<string> regressions)
    {
        if (before.Status == EvaluationStatus.Passed && after.Status != EvaluationStatus.Passed)
        {
            regressions.Add($"{after.Id}: status {before.Status} -> {after.Status}");
        }

        if (before.Outcome != after.Outcome)
        {
            regressions.Add($"{after.Id}: outcome {before.Outcome} -> {after.Outcome}");
        }

        if (!before.IsUnverified && after.IsUnverified)
        {
            regressions.Add($"{after.Id}: turn became unverified");
        }

        Increased(after.Id, "iterations", before.Iterations, after.Iterations, regressions);
        Increased(after.Id, "tool calls", before.ToolCalls, after.ToolCalls, regressions);
        Increased(after.Id, "tool errors", before.ToolErrors, after.ToolErrors, regressions);
        Increased(after.Id, "tool refusals", before.ToolRefusals, after.ToolRefusals, regressions);
        Increased(after.Id, "unexpected changes", before.UnexpectedChanges.Count, after.UnexpectedChanges.Count, regressions);
        Usage(after.Id, "input", before.InputTokens, after.InputTokens, regressions);
        Usage(after.Id, "output", before.OutputTokens, after.OutputTokens, regressions);
    }

    private static void Increased(string id, string metric, int before, int after, List<string> regressions)
    {
        if (after > before)
        {
            regressions.Add($"{id}: {metric} {before} -> {after}");
        }
    }

    private static void Usage(string id, string metric, int? before, int? after, List<string> regressions)
    {
        if (before.HasValue && !after.HasValue)
        {
            regressions.Add($"{id}: {metric} tokens became unknown");
        }
        else if (before.HasValue && after.HasValue)
        {
            Increased(id, $"{metric} tokens", before.Value, after.Value, regressions);
        }
    }
}
