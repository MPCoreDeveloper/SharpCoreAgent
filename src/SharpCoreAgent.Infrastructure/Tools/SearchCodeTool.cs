using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Answers "where in this codebase is this?" with ranked, line-addressed passages.
/// </summary>
/// <remarks>
/// This is the tool the model should reach for before <c>search_text</c>: it ranks by meaning as well as by words, it
/// follows the symbol graph one hop, and every block it returns is quotable (<c>path:start-end</c>) and hash-addressed,
/// so a later turn can tell whether the evidence is still the same code.
/// <para>
/// The tool knows nothing about storage. It holds the read side of the index and asks it one question; everything about
/// ranking, fusion and boosting lives behind <see cref="ICodeIndex"/>, where a different engine can replace it.
/// </para>
/// </remarks>
public sealed class SearchCodeTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "search_code";

    private const string Schema =
        """{"type":"object","properties":{"query":{"type":"string","description":"What to find: words, identifiers or a question."},"path":{"type":"string","description":"Limit to a file or directory relative to the workspace root."},"kind":{"type":"string","enum":["any","class","interface","record","struct","enum","method","property","field","doc","config"],"description":"Restrict to one declaration kind. Defaults to any."},"k":{"type":"integer","minimum":1,"maximum":12,"description":"How many blocks to return. Defaults to 8."},"hops":{"type":"integer","minimum":0,"maximum":3,"description":"How far to expand through the symbol graph before ranking. Defaults to 1."}},"required":["query"],"additionalProperties":false}""";

    private readonly ICodeIndex? _index;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="index">
    /// The read side of the code index, or <see langword="null"/> when the host started with <c>--no-index</c>. A null
    /// index is not a construction error: the tool then answers with the documented "disabled" failure, which is how
    /// the model learns to fall back to <c>search_text</c>.
    /// </param>
    public SearchCodeTool(ICodeIndex? index = null) => _index = index;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Search the code index for passages that answer a question. Returns ranked blocks as path:start-end with a hash and the retrieval legs that found them. Use it first for 'where is X' and 'how does X work' questions; fall back to search_text only when the index is missing.",
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
        var text = arguments.GetRequiredString("query");
        var kind = CodeIndexAccess.KindName(
            arguments.GetOptionalString("kind"),
            CodeQuery.SupportedKinds,
            Name,
            "kind");
        var k = arguments.GetOptionalInt("k", CodeQuery.DefaultK, minimum: 1, maximum: CodeQuery.MaxK);
        var hops = arguments.GetOptionalInt("hops", CodeQuery.DefaultHops, minimum: 0, maximum: CodeQuery.MaxHops);

        var path = CodeIndexAccess.ResolvePath(
            arguments.GetOptionalString("path"),
            context.Workspace,
            toolCall.Id,
            Name,
            out var refusal);

        if (refusal is not null)
        {
            return refusal;
        }

        var (failure, status) = await CodeIndexAccess
            .CheckAsync(_index, toolCall.Id, Name, context.Workspace.Root, cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        var query = CodeQuery.Create(text, path, kind, k, hops);
        var blocks = await _index!.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        return CodeIndexAccess.Answer(toolCall.Id, Name, Render(query, status, blocks), CodeIndexFormat.MaxOutputChars);
    }

    /// <summary>
    /// Renders one search answer.
    /// </summary>
    /// <param name="query">The query that was asked, after validation.</param>
    /// <param name="status">The index the answer came from.</param>
    /// <param name="blocks">The ranked blocks.</param>
    /// <returns>
    /// A heading naming how much was found and how healthy the index was, then one section per block.
    /// </returns>
    /// <remarks>
    /// "found 3 blocks (asked 8)" reports both numbers because neither alone is honest: three when eight were wanted says
    /// the index ran out, eight says it did not. The heading also repeats the index's freshness, so a model can decide
    /// whether to trust a line range without a second call.
    /// </remarks>
    internal static string Render(CodeQuery query, IndexStatus status, IReadOnlyList<CodeEvidence> blocks)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(blocks);

        if (blocks.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"no blocks matched '{query.Text}' in the index ({CodeIndexFormat.Number(status.Files)} files, {CodeIndexFormat.Number(status.Chunks)} passages)");
        }

        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"found {blocks.Count} {(blocks.Count == 1 ? "block" : "blocks")} (asked {query.K})");
        builder.Append(CultureInfo.InvariantCulture, $" · index {CodeIndexFormat.Freshness(status.Freshness)}");
        builder.Append(CultureInfo.InvariantCulture, $" · {status.StaleFiles} stale");
        builder.Append(CultureInfo.InvariantCulture, $" · {CodeIndexFormat.Legs(status)}");

        if (status.Freshness == IndexFreshness.Building)
        {
            builder.Append(" · ").Append(CodeIndexAccess.BusyNote);
        }

        if (status.Freshness == IndexFreshness.Partial)
        {
            builder.Append(CultureInfo.InvariantCulture, $" · coverage partial ({CodeIndexFormat.Number(status.Files)} files indexed so far)");
        }

        for (var index = 0; index < blocks.Count; index++)
        {
            builder
                .Append("\n\n")
                .Append(CodeIndexFormat.BlockHeading(index + 1, blocks[index]))
                .Append('\n')
                .Append(CodeIndexFormat.Excerpt(blocks[index], CodeIndexFormat.SearchExcerptLines));
        }

        return builder.ToString();
    }
}
