namespace SharpCoreAgent.Evaluation;

/// <summary>The execution mode and limits one evaluation run uses.</summary>
internal sealed record EvaluationExecution
{
    public bool Live { get; init; }
    public string Provider { get; init; } = "demo";
    public string? Model { get; init; }
    public string? BaseUrl { get; init; }
    public string? CredentialFile { get; init; }
    public int MaxIterations { get; init; } = 40;
    public int MaxOutputTokens { get; init; } = 4_000;
    public long MaxTotalTokens { get; init; } = 250_000;

    public string Mode => Live ? "live" : "recorded";
}
