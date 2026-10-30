using System.Globalization;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The words a delegation's answer is built from: the header, the outcome sentences and the approval preview.
/// </summary>
/// <remarks>
/// In one place for the reason <c>CodeFormat</c> exists: the tool, the tests and the contract document have to say
/// "4 model calls" the same way, and a rewording has to happen once. Every number is formatted with the invariant
/// culture, so a transcript recorded on a machine with a comma decimal separator reads like one recorded anywhere.
/// </remarks>
internal static class TaskFormat
{
    /// <summary>Gets the word used when the provider did not report what the child spent.</summary>
    internal const string UnknownTokens = "unknown";

    /// <summary>Gets what a completed sub-agent that said nothing is reported as.</summary>
    internal const string Nothing = "the sub-agent completed without saying anything.";

    /// <summary>
    /// Builds the line shown when the child starts.
    /// </summary>
    /// <param name="description">The delegation's label.</param>
    /// <param name="iterations">The ceiling the child was given.</param>
    /// <returns>One line, no newline.</returns>
    internal static string Started(string description, int iterations) => string.Create(
        CultureInfo.InvariantCulture,
        $"task · {description} · sub-agent started, at most {iterations} model calls");

    /// <summary>
    /// Builds the first line of the result: what was delegated and what it cost.
    /// </summary>
    /// <param name="description">The delegation's label.</param>
    /// <param name="iterations">The model calls the child actually used.</param>
    /// <param name="usage">What the child's turn consumed.</param>
    /// <returns>One line, no newline.</returns>
    internal static string Header(string description, int iterations, TokenUsage usage) => string.Create(
        CultureInfo.InvariantCulture,
        $"task · {description} · {iterations} model calls · {Tokens(usage)} tokens");

    /// <summary>
    /// Builds the line shown when the child ends.
    /// </summary>
    /// <param name="description">The delegation's label.</param>
    /// <param name="report">The child's report.</param>
    /// <returns>The same header the result will carry.</returns>
    internal static string Finished(string description, AgentReport report) =>
        Header(description, report.Iterations, report.Usage);

    /// <summary>
    /// Renders a turn's token total, or the word for a provider that did not say.
    /// </summary>
    /// <param name="usage">The usage to render.</param>
    /// <returns><c>18,940</c>, or <see cref="UnknownTokens"/>.</returns>
    internal static string Tokens(TokenUsage usage) => usage.Total is { } total
        ? total.ToString("n0", CultureInfo.InvariantCulture)
        : UnknownTokens;

    /// <summary>
    /// Builds the sentence that replaces the answer when the child did not finish.
    /// </summary>
    /// <param name="outcome">How the child's turn ended.</param>
    /// <param name="iterations">The ceiling the child was given, named so the caller can correct it.</param>
    /// <param name="detail">The child's own explanation, appended when it has one.</param>
    /// <returns>One sentence, naming the outcome.</returns>
    internal static string NotCompleted(AgentOutcome outcome, int iterations, string? detail)
    {
        var reason = string.IsNullOrWhiteSpace(detail) ? string.Empty : string.Concat(" ", detail.Trim());

        return outcome switch
        {
            AgentOutcome.BudgetExhausted => string.Create(
                CultureInfo.InvariantCulture,
                $"the sub-agent ran out of its {iterations} model calls without answering (BudgetExhausted).{reason}"),
            AgentOutcome.Failed => string.Concat("the sub-agent could not run (Failed).", reason),
            _ => "the sub-agent was cancelled before it answered.",
        };
    }

    /// <summary>
    /// Builds what the developer reads at the approval prompt.
    /// </summary>
    /// <param name="description">The delegation's label.</param>
    /// <param name="iterations">The ceiling the child will be given.</param>
    /// <param name="prompt">The instruction the child will see.</param>
    /// <returns>The label and the ceiling, then the prompt verbatim.</returns>
    /// <remarks>
    /// The prompt is not quoted, wrapped or indented: it is the instruction, and it reads the way the child will
    /// read it.
    /// </remarks>
    internal static string Preview(string description, int iterations, string prompt) => string.Create(
        CultureInfo.InvariantCulture,
        $"sub-agent · {description} · at most {iterations} model calls\n{prompt}");
}
