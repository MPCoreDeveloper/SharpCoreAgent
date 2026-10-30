using System.Globalization;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Code;

/// <summary>
/// The shared vocabulary of a code-intelligence answer: how a failure, a count and a severity are spelled.
/// </summary>
/// <remarks>
/// Three tools and one router answer from these ports, and a model that reads <c>2 uses</c> from one of them has to
/// read it from all of them. The words live here for the same reason <c>CodeIndexFormat</c> owns the retrieval
/// vocabulary: a test asserts one function instead of four literals, and a rewording happens once.
/// <para>
/// Every number is formatted with the invariant culture, so a transcript recorded on a machine with a comma decimal
/// separator reads exactly like one recorded anywhere else.
/// </para>
/// </remarks>
internal static class CodeFormat
{
    /// <summary>
    /// Renders a failure as the sentence a model reads, hint included.
    /// </summary>
    /// <param name="failure">The failure the adapter or the router produced.</param>
    /// <returns>
    /// <c>CODE: message</c>, then <c>next: hint</c> on its own line when the failure names one.
    /// </returns>
    /// <remarks>
    /// The code stays on the first line because it is what a test asserts and what a reader can look up; the hint is a
    /// second line because it is an instruction, and an instruction buried mid-sentence is not read.
    /// </remarks>
    internal static string Failure(CodeFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return string.IsNullOrEmpty(failure.Hint)
            ? string.Concat(failure.Code, ": ", failure.Message)
            : string.Concat(failure.Code, ": ", failure.Message, "\nnext: ", failure.Hint);
    }

    /// <summary>
    /// Renders a count with the noun it counts, singular or plural.
    /// </summary>
    /// <param name="count">The number.</param>
    /// <param name="singular">The noun for one, for example <c>use</c>.</param>
    /// <param name="plural">The noun for any other number; null appends an <c>s</c>.</param>
    /// <returns><c>1 use</c>, <c>2 uses</c>, or <c>0 uses</c>.</returns>
    /// <remarks>
    /// A word that does not pluralise with an <c>s</c> — <c>info</c> — passes its plural explicitly rather than
    /// teaching this method a list of exceptions.
    /// </remarks>
    internal static string Count(int count, string singular, string? plural = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(singular);

        var noun = count == 1 ? singular : plural ?? string.Concat(singular, "s");

        return string.Create(CultureInfo.InvariantCulture, $"{count} {noun}");
    }

    /// <summary>
    /// Builds one successful code-intelligence answer, shortened to the capability's character budget.
    /// </summary>
    /// <param name="callId">The tool call being answered.</param>
    /// <param name="toolName">The tool name, echoed back.</param>
    /// <param name="path">The file the question was about, which is the effect's target.</param>
    /// <param name="content">The rendered answer.</param>
    /// <returns>The result, with <see cref="ToolResult.IsTruncated"/> set when the budget cut it.</returns>
    /// <remarks>
    /// The effect names the file rather than <c>.</c>, unlike the index tools: this answer was produced by reading one
    /// source file, and the turn's summary can say which.
    /// </remarks>
    internal static ToolResult Answer(string callId, string toolName, string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var text = TextLimits.Bound(content, TextLimits.MaxCodeIntelligenceChars, out var truncated);

        return ToolResult.Success(callId, toolName, text, new ToolEffect(ToolEffectKind.Read, path)) with
        {
            IsTruncated = truncated,
        };
    }

    /// <summary>
    /// Renders a severity as the lowercase word a diagnostic line uses.
    /// </summary>
    /// <param name="severity">The severity.</param>
    /// <returns><c>error</c>, <c>warning</c> or <c>info</c>.</returns>
    internal static string Severity(CodeSeverity severity) => severity switch
    {
        CodeSeverity.Error => "error",
        CodeSeverity.Warning => "warning",
        CodeSeverity.Info => "info",
        _ => "unknown",
    };
}
