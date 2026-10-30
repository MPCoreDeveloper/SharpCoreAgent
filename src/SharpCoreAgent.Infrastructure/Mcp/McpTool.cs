using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// One tool a server offered, exposed to the model as an ordinary <see cref="ITool"/>.
/// </summary>
/// <remarks>
/// The adapter is deliberately thin, because everything interesting already happened: the server owns the
/// schema, the behaviour and the answer, and this type's job is to translate names, arguments and results
/// into the shapes round 001 fixed. Nothing here knows MCP exists beyond the prefix it was given, which is
/// why the loop, the approval model and the transcript needed no change to support it.
/// <para>
/// Two rules are worth stating. The schema is forwarded verbatim, because it is the server's contract with
/// the model. And an unannotated tool is destructive: the cost of a needless prompt is one keystroke and
/// the cost of a silent write is the file.
/// </para>
/// </remarks>
public sealed class McpTool : ITool
{
    private readonly McpToolInfo _info;
    private readonly McpServerConnection _connection;
    private readonly McpClientOptions _options;

    /// <summary>
    /// Creates the adapter for one server tool.
    /// </summary>
    /// <param name="name">The <c>&lt;server&gt;_&lt;tool&gt;</c> name the model will call.</param>
    /// <param name="info">What the server said about the tool at startup.</param>
    /// <param name="connection">The running server the call goes to.</param>
    /// <param name="options">The ceilings: the call budget and the result ceiling.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public McpTool(string name, McpToolInfo info, McpServerConnection connection, McpClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        _info = info;
        _connection = connection;
        _options = options;

        Definition = new ToolDefinition(name, Describe(info, connection), info.SchemaJson);
        Safety = info.ReadOnly ? ToolSafety.ReadOnly : ToolSafety.Destructive;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <inheritdoc />
    public ToolSafety Safety { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Failures come back as results rather than exceptions, as the tool contract requires: a server that
    /// misbehaves costs one call, and the model reads why instead of the turn ending.
    /// </remarks>
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        // The reader is the argument-object check: a call whose arguments are not an object becomes a
        // readable tool error naming what arrived, before anything is sent to the server.
        var argumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson;
        using var arguments = ToolArgumentReader.Parse(Definition.Name, argumentsJson);

        // One sink per call: it holds the artefacts written so far, so the per-call ceiling counts this
        // answer and only this answer.
        var sink = new McpArtefactSink(context.Workspace, context.Approval, _connection.Name, _info.Name, _options);

        var answer = await _connection
            .CallAsync(_info.Name, arguments.Root, _options.CallTimeout, sink.Save, cancellationToken)
            .ConfigureAwait(false);

        // Two ceilings, and this is the first: the tool's own. The loop's MaxToolResultChars still applies
        // to the turn afterwards, and the marker tells the model which one it hit.
        var text = TextLimits.Bound(answer.Text, _options.MaxResultChars, out var truncated);

        if (answer.IsError)
        {
            return ToolResult.Failure(toolCall.Id, Definition.Name, text) with { IsTruncated = truncated };
        }

        var effects = new List<ToolEffect>();
        if (Safety == ToolSafety.Destructive)
        {
            effects.Add(new ToolEffect(
                ToolEffectKind.CommandRun,
                string.Create(CultureInfo.InvariantCulture, $"{_connection.Name}: {_info.Name}")));
        }

        // A screenshot is annotated read-only by every server that offers one, and it still writes a file.
        // The effects say so, because a turn that reports what it touched must not hide the one write it made.
        foreach (var relative in sink.Written)
        {
            effects.Add(new ToolEffect(ToolEffectKind.Created, relative));
        }

        return ToolResult.Success(toolCall.Id, Definition.Name, text, [.. effects]) with { IsTruncated = truncated };
    }

    private static string Describe(McpToolInfo info, McpServerConnection connection) =>
        string.IsNullOrWhiteSpace(info.Description)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Call the '{info.Name}' tool of the {connection.Name} MCP server, started with: {connection.CommandLine}")
            : info.Description;
}
