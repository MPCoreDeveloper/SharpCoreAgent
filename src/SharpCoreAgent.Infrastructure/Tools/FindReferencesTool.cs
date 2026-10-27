using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Finds the code that calls a declaration, or the code it calls, by following the index's typed edges.
/// </summary>
/// <remarks>
/// This is the question text search cannot answer: <c>search_text</c> finds every occurrence of a name, including the
/// declaration itself, the string literals and the comments. The index knows which occurrences are call sites, so this
/// answer is short and every block carries the relation that reached it (<c>rel calls</c>, <c>rel called_by</c>), which
/// is what tells a reader which direction the edge runs in.
/// </remarks>
public sealed class FindReferencesTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "find_references";

    private const int MaxResults = 100;

    private const string Schema =
        """{"type":"object","properties":{"symbol":{"type":"string","description":"Exact or qualified symbol name, as returned by find_symbol."},"direction":{"type":"string","enum":["callers","callees","both"],"description":"Defaults to callers."},"max_results":{"type":"integer","minimum":1,"maximum":100,"description":"Defaults to 40."}},"required":["symbol"],"additionalProperties":false}""";

    private readonly ICodeIndex? _index;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="index">The read side of the code index, or <see langword="null"/> when the host disabled it.</param>
    public FindReferencesTool(ICodeIndex? index = null) => _index = index;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Find what calls a declaration (direction=callers), what it calls (direction=callees), or both. Returns blocks grouped by file with the relation that reached each one. Use the qualified name find_symbol printed.",
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
        var symbol = arguments.GetRequiredString("symbol");
        var direction = ParseDirection(arguments.GetOptionalString("direction"));
        var maxResults = arguments.GetOptionalInt("max_results", 40, minimum: 1, maximum: MaxResults);

        var (failure, _) = await CodeIndexAccess
            .CheckAsync(_index, toolCall.Id, Name, context.Workspace.Root, cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        var subject = await ResolveNameAsync(_index!, symbol, cancellationToken).ConfigureAwait(false);
        var blocks = await _index!
            .FindReferencesAsync(subject, direction, maxResults, cancellationToken)
            .ConfigureAwait(false);

        return CodeIndexAccess.Answer(
            toolCall.Id,
            Name,
            Render(subject, direction, blocks),
            CodeIndexFormat.MaxOutputChars);
    }

    /// <summary>
    /// Resolves the name the model wrote to the qualified name the index uses, when it can.
    /// </summary>
    /// <param name="index">The index to ask.</param>
    /// <param name="symbol">The name as the model wrote it.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The qualified name of the best match, or <paramref name="symbol"/> when nothing matched.</returns>
    /// <remarks>
    /// The answer's heading has to name one declaration, and a short name like <c>TurnAsync</c> may exist in three
    /// types. Asking the symbol leg first turns the heading into the declaration the references actually belong to
    /// instead of echoing the fragment back, which is the difference between an answer a reader can verify and one they
    /// have to guess at.
    /// </remarks>
    private static async ValueTask<string> ResolveNameAsync(
        ICodeIndex index,
        string symbol,
        CancellationToken cancellationToken)
    {
        var matches = await index.FindSymbolsAsync(symbol, null, cancellationToken).ConfigureAwait(false);

        var best = matches.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, symbol, StringComparison.Ordinal) ||
            string.Equals(candidate.QualifiedName, symbol, StringComparison.Ordinal) ||
            string.Equals(candidate.Key, symbol, StringComparison.Ordinal));

        return best?.QualifiedName ?? (matches.Count > 0 ? matches[0].QualifiedName : symbol.Trim());
    }
    /// <summary>
    /// Parses the <c>direction</c> argument.
    /// </summary>
    /// <param name="value">The argument, or <see langword="null"/> for the default.</param>
    /// <returns>The direction the answer follows.</returns>
    /// <exception cref="ToolArgumentException">The value is not one of the three documented directions.</exception>
    private static ReferenceDirection ParseDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReferenceDirection.Callers;
        }

        var trimmed = value.Trim();

        if (string.Equals(trimmed, "callers", StringComparison.OrdinalIgnoreCase))
        {
            return ReferenceDirection.Callers;
        }

        if (string.Equals(trimmed, "callees", StringComparison.OrdinalIgnoreCase))
        {
            return ReferenceDirection.Callees;
        }

        if (string.Equals(trimmed, "both", StringComparison.OrdinalIgnoreCase))
        {
            return ReferenceDirection.Both;
        }

        throw new ToolArgumentException(Name, "direction", $"must be one of callers, callees, both, but was '{trimmed}'");
    }

    /// <summary>
    /// Renders one reference answer.
    /// </summary>
    /// <param name="qualified">The declaration the references belong to, after resolution.</param>
    /// <param name="direction">Which side of the relation was followed.</param>
    /// <param name="blocks">The blocks the walk reached, labelled with their relation.</param>
    /// <returns>A heading, then the blocks grouped per file in ordinal path order and line order.</returns>
    /// <remarks>
    /// Grouping by file is what makes the answer usable as a plan: a reader who has to change a signature wants to know
    /// which files to open, and the count of files is that list's length. Blocks inside one file stay in line order so
    /// the answer reads like the file does.
    /// </remarks>
    internal static string Render(string qualified, ReferenceDirection direction, IReadOnlyList<CodeEvidence> blocks)
    {
        ArgumentNullException.ThrowIfNull(qualified);
        ArgumentNullException.ThrowIfNull(blocks);

        var noun = Noun(direction, blocks.Count);

        if (blocks.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"no {noun} of {qualified} in the index");
        }

        var files = blocks
            .Select(block => block.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{blocks.Count} {noun} of {qualified}");
        builder.Append(CultureInfo.InvariantCulture, $" · {files.Count} {(files.Count == 1 ? "file" : "files")}");
        builder.Append(CultureInfo.InvariantCulture, $" · {blocks.Count} listed");

        foreach (var path in files)
        {
            builder.Append("\n\n").Append(path);

            foreach (var block in blocks
                         .Where(block => string.Equals(block.Path, path, StringComparison.Ordinal))
                         .OrderBy(block => block.StartLine)
                         .ThenBy(block => block.EndLine))
            {
                builder
                    .Append("\n  ")
                    .Append(block.StartLine.ToString(CultureInfo.InvariantCulture))
                    .Append('-')
                    .Append(block.EndLine.ToString(CultureInfo.InvariantCulture))
                    .Append(" · ")
                    .Append(block.Kind);

                if (!string.IsNullOrEmpty(block.Symbol))
                {
                    builder.Append(' ').Append(block.Symbol);
                }

                builder
                    .Append(" · rel ")
                    .Append(block.Relation ?? (direction == ReferenceDirection.Callees ? "called_by" : "calls"))
                    .Append(" · ")
                    .Append(string.IsNullOrEmpty(block.Sha8) ? "unindexed" : string.Concat("sha256:", block.Sha8))
                    .Append('\n')
                    .Append(CodeIndexFormat.Excerpt(block, CodeIndexFormat.ReferenceExcerptLines));
            }
        }

        return builder.ToString();
    }

    private static string Noun(ReferenceDirection direction, int count) => direction switch
    {
        ReferenceDirection.Callers => count == 1 ? "caller" : "callers",
        ReferenceDirection.Callees => count == 1 ? "callee" : "callees",
        _ => count == 1 ? "reference" : "references",
    };
}
