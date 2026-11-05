using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// Lists a server's resources, or reads one by URI.
/// </summary>
/// <remarks>
/// MCP resources are addressed by URI rather than by a tool name, so the model needs one small bridge: a call with no
/// argument lists what the server offers, and a call with <c>uri</c> reads exactly one resource. The answer is text;
/// binary resources are described rather than written, because a resource URI is not an artefact path and a blob is
/// not a screenshot the model can open.
/// </remarks>
public sealed class McpResourceTool : ITool
{
    private const string Schema =
        """{"type":"object","properties":{"uri":{"type":"string","description":"A resource URI from the list; omit to list the resources instead."}},"additionalProperties":false}""";

    private readonly McpServerConnection _connection;
    private readonly McpClientOptions _options;

    /// <summary>Creates the bridge for one server.</summary>
    /// <param name="name">The tool name, already prefixed and unique.</param>
    /// <param name="connection">The running server.</param>
    /// <param name="options">The result ceiling.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public McpResourceTool(string name, McpServerConnection connection, McpClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        Definition = new ToolDefinition(
            name,
            $"List the resources published by the {connection.Name} MCP server, or read one by URI. Call it with no arguments to discover the URIs; call it with uri to read that resource. Binary resources are described by type and size rather than returned as bytes.",
            Schema);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

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

        using var arguments = ToolArgumentReader.Parse(Definition.Name, toolCall.ArgumentsJson);
        var uri = arguments.GetOptionalString("uri");
        if (uri is not null && string.IsNullOrWhiteSpace(uri))
        {
            return ToolResult.Failure(toolCall.Id, Definition.Name, "uri must not be empty");
        }

        try
        {
            var result = uri is null
                ? await _connection.ListResourcesAsync(cancellationToken).ConfigureAwait(false)
                : await _connection.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false);

            var content = uri is null ? FormatList(result) : FormatRead(result);
            content = TextLimits.Bound(content, _options.MaxResultChars, out var truncated);
            return ToolResult.Success(toolCall.Id, Definition.Name, content) with { IsTruncated = truncated };
        }
        catch (ToolArgumentException ex)
        {
            return ToolResult.Failure(toolCall.Id, Definition.Name, ex.Message);
        }
        catch (Exception ex) when (ex is McpTransportException or JsonRpcException or InvalidOperationException or ArgumentException)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Definition.Name,
                string.Create(CultureInfo.InvariantCulture, $"the {_connection.Name} resources bridge failed: {ex.Message}"));
        }
    }
 
    private static string FormatList(JsonElement result)
    {
        if (!result.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            return "The server returned no resources list.";
        }

        if (resources.GetArrayLength() == 0)
        {
            return "The server exposes no resources.";
        }

        var text = new StringBuilder("Resources:\n");
        foreach (var entry in resources.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var uri = String(entry, "uri") ?? "(no uri)";
            text.Append("- ").Append(uri);
            if (String(entry, "name") is { } name)
            {
                text.Append(" — ").Append(OneLine(name));
            }

            if (String(entry, "mimeType") is { } mime)
            {
                text.Append(" (").Append(mime).Append(')');
            }

            if (String(entry, "description") is { } description)
            {
                text.Append(": ").Append(OneLine(description));
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    private static string FormatRead(JsonElement result)
    {
        if (!result.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
        {
            return "The server returned no contents.";
        }

        if (contents.GetArrayLength() == 0)
        {
            return "The resource is empty.";
        }

        var text = new StringBuilder();
        foreach (var entry in contents.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            var uri = String(entry, "uri") ?? "(no uri)";
            var mime = String(entry, "mimeType");
            if (String(entry, "text") is { } body)
            {
                text.Append(CultureInfo.InvariantCulture, $"[resource: {uri}{(mime is null ? string.Empty : $" · {mime}")}]\n{body}");
            }
            else if (String(entry, "blob") is { } blob)
            {
                text.Append(CultureInfo.InvariantCulture, $"[binary resource: {uri} · {mime ?? "unknown type"} · {blob.Length} base64 characters]");
            }
            else
            {
                text.Append(CultureInfo.InvariantCulture, $"[resource: {uri} · no content]");
            }
        }

        return text.ToString();
    }

    private static string? String(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}

