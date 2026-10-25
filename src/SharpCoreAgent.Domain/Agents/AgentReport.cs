using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Domain.Agents;

/// <summary>
/// How a turn ended.
/// </summary>
/// <remarks>
/// A turn ends in exactly one of these states, and the CLI maps them onto exit codes. Refusal of an
/// individual tool is <em>not</em> a terminal state: the refusal is reported to the model, which
/// usually adapts, so the turn keeps running and lands in <see cref="Completed"/>.
/// </remarks>
public enum AgentOutcome
{
    /// <summary>The model finished its answer.</summary>
    Completed,

    /// <summary>A budget ran out: iterations, tokens or wall-clock time.</summary>
    BudgetExhausted,

    /// <summary>The developer interrupted the turn.</summary>
    Cancelled,

    /// <summary>The turn could not run or the provider failed unrecoverably.</summary>
    Failed,
}

/// <summary>
/// The durable record of one turn.
/// </summary>
/// <remarks>
/// The report is what the CLI prints, what a test asserts on and what a session persists. It carries
/// the effect list rather than a diff because a turn may touch files the agent cannot show (a file it
/// only read, a command it only ran), and the developer still needs to know.
/// </remarks>
public sealed record AgentReport
{
    /// <summary>Gets how the turn ended.</summary>
    public required AgentOutcome Outcome { get; init; }

    /// <summary>Gets the <c>provider:model</c> that served the turn.</summary>
    public required string ModelId { get; init; }

    /// <summary>Gets the model's final text, when there was one.</summary>
    public string? FinalText { get; init; }

    /// <summary>Gets the usage consumed by this turn, not by the session.</summary>
    public TokenUsage Usage { get; init; } = TokenUsage.Unknown;

    /// <summary>Gets how many model round trips the turn used.</summary>
    public int Iterations { get; init; }

    /// <summary>Gets every tool result produced during the turn, in order.</summary>
    public IReadOnlyList<ToolResult> ToolResults { get; init; } = [];

    /// <summary>Gets a one-line explanation for a non-completed outcome.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets a value indicating whether the turn completed.</summary>
    public bool Succeeded => Outcome == AgentOutcome.Completed;

    /// <summary>Gets the workspace-relative files the turn created, modified or deleted, in order of first mention.</summary>
    public IReadOnlyList<string> FilesTouched => CollectFiles();

    /// <summary>Gets the commands the turn ran, in order.</summary>
    public IReadOnlyList<string> CommandsRun => CollectCommands();

    private List<string> CollectFiles() => ToolResults
        .SelectMany(result => result.Effects)
        .Where(effect => effect.Kind is ToolEffectKind.Created or ToolEffectKind.Modified or ToolEffectKind.Deleted)
        .Select(effect => effect.Target)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    private List<string> CollectCommands()
    {
        if (ToolResults.Count == 0)
        {
            return [];
        }

        var commands = new List<string>(ToolResults.Count);
        foreach (var result in ToolResults)
        {
            foreach (var effect in result.Effects)
            {
                if (effect.Kind == ToolEffectKind.CommandRun)
                {
                    commands.Add(effect.Target);
                }
            }
        }

        return commands;
    }
}
