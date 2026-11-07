using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Finds documentation sections and reports the declaration each one describes.
/// </summary>
/// <remarks>
/// This is the prose counterpart of <c>find_symbol</c>: the same "I know what I am looking for, tell me where it is"
/// question, asked of the Markdown and doc-comments the index already stored. It answers where <c>search_code</c> ranks
/// by meaning, and it adds the one thing a passage cannot say on its own — which declaration a section is <em>about</em>,
/// because the build attached a section whose heading names a symbol to that symbol.
/// </remarks>
public sealed class FindDocsTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "find_docs";

    private const int MaxResults = 50;

    private const string Schema =
        """{"type":"object","properties":{"query":{"type":"string","description":"Words or a phrase to find in documentation headings or text."},"max_results":{"type":"integer","minimum":1,"maximum":50,"description":"Defaults to 20."}},"required":["query"],"additionalProperties":false}""";

    private readonly ICodeIndex? _index;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="index">The read side of the code index, or <see langword="null"/> when the host disabled it.</param>
    public FindDocsTool(ICodeIndex? index = null) => _index = index;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Find documentation sections (Markdown headings, doc comments) matching a phrase. Returns path:start-end, the heading, and the declaration each section describes. Use it to answer 'what do the docs say about X' and 'which symbols are documented'.",
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
        var query = arguments.GetRequiredString("query");
        var maxResults = arguments.GetOptionalInt("max_results", 20, minimum: 1, maximum: MaxResults);

        var (failure, status) = await CodeIndexAccess
            .CheckAsync(_index, toolCall.Id, Name, context.Workspace.Root, cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        var docs = await _index!.FindDocsAsync(query, maxResults, cancellationToken).ConfigureAwait(false);

        return CodeIndexAccess.Answer(toolCall.Id, Name, Render(query, status, docs), CodeIndexFormat.MaxOutputChars);
    }

    /// <summary>
    /// Renders one documentation answer.
    /// </summary>
    /// <param name="query">The phrase that was asked, as written.</param>
    /// <param name="status">The index the answer came from.</param>
    /// <param name="docs">The matching sections.</param>
    /// <returns>
    /// A heading naming how many sections were found and how fresh the index was, then one block per section: its
    /// citation, its heading, the declaration it describes, and the first line of prose.
    /// </returns>
    internal static string Render(string query, IndexStatus status, IReadOnlyList<CodeDocRef> docs)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(docs);

        if (docs.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"no documentation matched '{query}' in the index ({CodeIndexFormat.Number(status.Files)} files)");
        }

        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"{docs.Count} {(docs.Count == 1 ? "section" : "sections")} for '{query}'");
        builder.Append(CultureInfo.InvariantCulture, $" · index {CodeIndexFormat.Freshness(status.Freshness)}");

        foreach (var doc in docs)
        {
            builder.Append("\n\n");
            builder
                .Append(doc.Path)
                .Append(':')
                .Append(doc.StartLine.ToString(CultureInfo.InvariantCulture))
                .Append('-')
                .Append(doc.EndLine.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(doc.Heading))
            {
                builder.Append(" · ").Append(doc.Heading);
            }

            if (!string.IsNullOrEmpty(doc.Symbol))
            {
                builder.Append(" · describes ").Append(doc.Symbol);
            }

            if (FirstProseLine(doc.Text) is { Length: > 0 } line)
            {
                builder.Append("\n  ").Append(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Picks the first line of a section worth quoting: the first non-blank line that is not a heading.
    /// </summary>
    /// <param name="text">The section text.</param>
    /// <returns>The trimmed line, shortened to a sentence-sized width, or an empty string.</returns>
    private static string FirstProseLine(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            return line.Length <= 160 ? line : string.Concat(line[..160], "…");
        }

        return string.Empty;
    }
}
