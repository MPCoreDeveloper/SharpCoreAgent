using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reports what the index holds and how healthy it is, in three lines.
/// </summary>
/// <remarks>
/// This tool differs from its four siblings in one respect: a missing index is an answer, not a failure. The question is
/// about the index, so "state missing" is a complete reply and the model can decide what to do next — whereas
/// <c>search_code</c> cannot answer its question at all without one and says so as an error.
/// <para>
/// The whole point is brevity: three lines of coverage cost a fraction of a status document, and a model that can see
/// "412 files, 0 stale" can judge whether an empty search result means "not there" or "not indexed".
/// </para>
/// </remarks>
public sealed class IndexStatusTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "index_status";

    private const string Schema = """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

    private readonly ICodeIndex? _index;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="index">The read side of the code index, or <see langword="null"/> when the host disabled it.</param>
    public IndexStatusTool(ICodeIndex? index = null) => _index = index;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Report the state of the code index: freshness, tier, embedder, file and passage counts, and its size. Use it when a search came back empty or stale and you need to know whether the index can be trusted.",
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

        // Parsed even though the schema has no arguments: the reader is also what rejects a malformed call, and a
        // tool that skipped that check would be the one place where garbage argument text went unnoticed.
        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);

        if (_index is null)
        {
            return ToolResult.Failure(toolCall.Id, Name, CodeIndexAccess.DisabledMessage);
        }

        var status = await _index.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return CodeIndexAccess.Answer(
            toolCall.Id,
            Name,
            Render(status),
            CodeIndexFormat.MaxOutputChars);
    }

    /// <summary>
    /// Renders one status answer.
    /// </summary>
    /// <param name="status">The status to describe.</param>
    /// <returns>Three lines of prose, or one line saying there is no index.</returns>
    /// <remarks>
    /// The first line names the state and where the coverage came from; the second counts what is stored; the third says
    /// which legs can answer and which workspace the index describes. A model that reads only the first line still
    /// learns the one thing it needs: whether to trust a citation from this turn.
    /// </remarks>
    internal static string Render(IndexStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.Freshness == IndexFreshness.Missing)
        {
            return "state missing · no index at .sharpcoreagent/index; run `sharpcoreagent index`";
        }

        var builder = new StringBuilder();

        builder.Append("state ").Append(CodeIndexFormat.Freshness(status.Freshness));
        builder.Append(" · tier ").Append(status.Tier == IndexTier.Rich ? "rich" : "lean");
        builder.Append(" · embedder ").Append(status.Embedder);

        if (status.Dimensions > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $" · dimensions {status.Dimensions}");
        }

        builder.Append(" · built ").Append(status.BuiltUtc is { } built
            ? built.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "never");

        builder.Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"files {CodeIndexFormat.Number(status.Files)} ({status.StaleFiles} stale, {status.SkippedFiles} skipped)");
        builder.Append(CultureInfo.InvariantCulture, $" · passages {CodeIndexFormat.Number(status.Chunks)}");
        builder.Append(CultureInfo.InvariantCulture, $" · symbols {CodeIndexFormat.Number(status.Symbols)}");
        builder.Append(CultureInfo.InvariantCulture, $" · edges {CodeIndexFormat.Number(status.Edges)}");
        builder.Append(" · size ").Append(CodeIndexFormat.Size(status.SizeBytes));

        builder.Append('\n');
        builder.Append("vector search ").Append(status.VectorSearchEnabled && !status.LexicalFallback ? "on" : "off");
        builder.Append(" · lexical index on");
        builder.Append(" · graph on");
        builder.Append(" · workspace ").Append(status.WorkspaceRoot.Replace('\\', '/'));

        return builder.ToString();
    }
}
