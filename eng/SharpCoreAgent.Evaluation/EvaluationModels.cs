using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpCoreAgent.Evaluation;

internal sealed record EvaluationScenario
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Language { get; init; }
    public required string Prompt { get; init; }
    public required IReadOnlyList<RecordedTurn> Turns { get; init; }
    public required IReadOnlyList<string> AllowedChanges { get; init; }
    public required ScenarioVerification Verification { get; init; }
    public required ScenarioGrade Grade { get; init; }

    [JsonIgnore]
    public string Directory { get; init; } = string.Empty;

    [JsonIgnore]
    public string WorkspacePath => Path.Combine(Directory, "workspace");

    [JsonIgnore]
    public string GradingPath => Path.Combine(Directory, "grading");
}

internal sealed record RecordedTurn
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<RecordedCall> Calls { get; init; } = [];
}

internal sealed record RecordedCall
{
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
}

internal sealed record ScenarioVerification
{
    public required string Tool { get; init; }
    public string? Command { get; init; }
}

internal sealed record ScenarioGrade
{
    public string? RuntimeProbe { get; init; }
    public required string Command { get; init; }
    public IReadOnlyList<string> ExpectedOutput { get; init; } = [];
    public IReadOnlyList<ExpectedFile> ExpectedFiles { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 120;
}

internal sealed record ExpectedFile
{
    public required string Path { get; init; }
    public required IReadOnlyList<string> Contains { get; init; }
}

internal sealed record EvaluationReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Mode { get; init; } = "recorded";
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required IReadOnlyList<ScenarioReport> Scenarios { get; init; }
    public required EvaluationSummary Summary { get; init; }
}

internal sealed record ScenarioReport
{
    public required string Id { get; init; }
    public required string Language { get; init; }
    public required string Status { get; init; }
    public required string Outcome { get; init; }
    public bool IsUnverified { get; init; }
    public int Iterations { get; init; }
    public int ToolCalls { get; init; }
    public int ToolErrors { get; init; }
    public int ToolRefusals { get; init; }
    public IReadOnlyList<WorkspaceChange> ActualChanges { get; init; } = [];
    public IReadOnlyList<string> UnexpectedChanges { get; init; } = [];
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public IReadOnlyList<string> CommandsRun { get; init; } = [];
    public string? FinalText { get; init; }
    public IReadOnlyList<EvaluationToolResult> ToolResults { get; init; } = [];
    public EvaluationToolResult? GradeResult { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = [];
}

internal sealed record EvaluationToolResult
{
    public required string CallId { get; init; }
    public required string Tool { get; init; }
    public required string Content { get; init; }
    public bool IsError { get; init; }
    public bool IsRefused { get; init; }
    public bool IsTruncated { get; init; }
}

internal sealed record EvaluationSummary
{
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Attempted { get; init; }
    public double? SuccessRate { get; init; }
    public int UnintendedChanges { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

internal sealed record WorkspaceChange(string Kind, string Path);

internal static class EvaluationStatus
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>Identifies invalid arguments, scenario data or baseline data that the maintainer can correct.</summary>
/// <param name="message">The actionable validation failure.</param>
public sealed class EvaluationException(string message) : Exception(message);
