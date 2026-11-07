using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Finds framework cues — routes, tests, dependency-injection registrations and database calls — in the indexed source.
/// </summary>
/// <remarks>
/// This answers the question the symbol and search tools cannot: "what does this service expose, and where does it
/// reach the database?". Every cue it returns is a candidate — a regular expression over the source the build stored —
/// so the tool says so and cites the line rather than claiming a resolved fact.
/// </remarks>
public sealed class FindCuesTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "find_cues";

    private const int MaxResults = 100;

    private const string Schema =
        """{"type":"object","properties":{"kind":{"type":"string","enum":["any","route","test","dependency_injection","database_call"],"description":"Which framework cue to find. Defaults to any."},"text":{"type":"string","description":"Optional text a cue's target, path or captured detail must contain."},"max_results":{"type":"integer","minimum":1,"maximum":100,"description":"Defaults to 40."}},"additionalProperties":false}""";

    private readonly ICodeIndex? _index;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="index">The read side of the code index, or <see langword="null"/> when the host disabled it.</param>
    public FindCuesTool(ICodeIndex? index = null) => _index = index;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Find framework cues in the indexed source as cited candidates: route registrations, tests, dependency-injection registrations and database calls. Returns path:line, the written expression, any captured route path, and the matched line. Cues are regular-expression matches, not resolved facts.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.ReadOnly;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var kind = CodeIndexAccess.CueKindName(arguments.GetOptionalString("kind"), Name, "kind");
        var text = arguments.GetOptionalString("text");
        var maxResults = arguments.GetOptionalInt("max_results", 40, minimum: 1, maximum: MaxResults);

        var (failure, status) = await CodeIndexAccess
            .CheckAsync(_index, toolCall.Id, Name, context.Workspace.Root, cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        var cues = await _index!.FindCuesAsync(kind, text, maxResults, cancellationToken).ConfigureAwait(false);

        return CodeIndexAccess.Answer(
            toolCall.Id,
            Name,
            Render(kind, text, status, cues),
            CodeIndexFormat.MaxOutputChars);
    }

    /// <summary>
    /// Renders one cue answer.
    /// </summary>
    /// <param name="kind">The canonical kind that was asked for.</param>
    /// <param name="text">The optional text filter, or <see langword="null"/>.</param>
    /// <param name="status">The index the answer came from.</param>
    /// <param name="cues">The matches.</param>
    /// <returns>A heading, then one block per cue with its citation and matched line.</returns>
    internal static string Render(string kind, string? text, IndexStatus status, IReadOnlyList<CodeCueRef> cues)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(cues);

        var label = string.Equals(kind, CueKinds.Any, StringComparison.Ordinal) ? "framework" : kind;
        var scope = string.IsNullOrWhiteSpace(text) ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" for '{text.Trim()}'");

        if (cues.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"no {label} cues{scope} found in the index ({CodeIndexFormat.Number(status.Files)} files)");
        }

        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"{cues.Count} {(cues.Count == 1 ? "cue" : "cues")} ({label}){scope}");
        builder.Append(CultureInfo.InvariantCulture, $" · index {CodeIndexFormat.Freshness(status.Freshness)}");

        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];

            builder.Append("\n\n");
            builder
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append(") ")
                .Append(cue.Kind)
                .Append(" · ")
                .Append(cue.Target);

            if (!string.IsNullOrEmpty(cue.Detail))
            {
                builder.Append(" · ").Append(cue.Detail);
            }

            builder
                .Append("\n  ")
                .Append(cue.Path)
                .Append(':')
                .Append(cue.Line.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(cue.Symbol))
            {
                builder.Append(" · ").Append(cue.Symbol);
            }

            builder.Append("\n    ").Append(cue.Snippet);
        }

        return builder.ToString();
    }
}
