using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// One tool this server is willing to answer for, with its schema already parsed.
/// </summary>
/// <param name="Tool">The tool itself, which is what a call runs.</param>
/// <param name="InputSchema">The tool's own schema, parsed from the text the provider receives.</param>
/// <remarks>
/// The schema is parsed here rather than at <c>tools/list</c> time for two reasons: a schema that is not JSON is a
/// construction failure instead of a broken answer to a client mid-session, and a client that asks for the list twice
/// gets the same object rather than a second parse.
/// </remarks>
internal sealed record McpServerPublishedTool(ITool Tool, JsonObject InputSchema);

/// <summary>
/// The exact set of tools this server publishes: a fixed allow-list intersected with the registry's read-only tools.
/// </summary>
/// <remarks>
/// Both gates are required, and the redundancy is the requirement rather than an oversight. The allow-list means a
/// future read-only tool cannot join the server because someone registered it; the safety filter means a name on the
/// list that later became destructive cannot be served. A name on the list that the registry does not hold is
/// skipped, never published from a stale schema.
/// <para>
/// Order is the registry's, which is ordinal, so <c>tools/list</c> is byte-identical between runs.
/// </para>
/// </remarks>
internal sealed class McpServerPublishedSet
{
    /// <summary>
    /// The only names this server may ever publish, whatever flags the host was started with.
    /// </summary>
    /// <remarks>
    /// Written in the order the contract documents, and held in <see cref="McpServerCatalog"/> rather than here because
    /// an agent profile now lists the same names: one array, two readers. Nothing outside it reaches a client — there is
    /// no <c>--read-tools</c>, and no flag names one of these tools.
    /// </remarks>
    internal static IReadOnlyList<string> AllowList => McpServerCatalog.PublishedToolNames;

    private readonly McpServerPublishedTool[] _tools;

    private McpServerPublishedSet() => _tools = [];

    /// <summary>
    /// Filters a registry down to what may be served.
    /// </summary>
    /// <param name="registry">The registry the host built; every tool in it is a candidate and most are not.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ArgumentException">A published tool's schema is not a JSON object.</exception>
    internal McpServerPublishedSet(IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _tools =
        [
            .. registry.Tools
                .Where(tool => tool.Safety == ToolSafety.ReadOnly)
                .Where(tool => AllowList.Contains(tool.Definition.Name, StringComparer.Ordinal))
                .Select(tool => new McpServerPublishedTool(tool, Parse(tool))),
        ];
    }

    /// <summary>Gets a set that publishes nothing, which is what a session built without a registry serves.</summary>
    internal static McpServerPublishedSet Empty { get; } = new();

    /// <summary>Gets the published tools, in the order <c>tools/list</c> reports them.</summary>
    internal IReadOnlyList<McpServerPublishedTool> Tools => _tools;

    /// <summary>Gets how many tools are published.</summary>
    internal int Count => _tools.Length;

    /// <summary>
    /// Finds a published tool by the name a client used.
    /// </summary>
    /// <param name="name">The requested name; matching is ordinal, so case matters as it does everywhere else.</param>
    /// <returns>The published tool, or <see langword="null"/> when it is not published.</returns>
    internal McpServerPublishedTool? Find(string? name) =>
        Array.Find(_tools, tool => string.Equals(tool.Tool.Definition.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Parses one tool's schema, refusing a schema the server could not publish honestly.
    /// </summary>
    /// <param name="tool">The tool whose <see cref="ToolDefinition.ParametersSchemaJson"/> is read.</param>
    /// <returns>The schema as a JSON object.</returns>
    /// <exception cref="ArgumentException">The text is not JSON, or is not a JSON object.</exception>
    private static JsonObject Parse(ITool tool)
    {
        var name = tool.Definition.Name;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(tool.Definition.ParametersSchemaJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"the parameter schema of '{name}' is not JSON: {ex.Message}", nameof(tool), ex);
        }

        return parsed as JsonObject
            ?? throw new ArgumentException($"the parameter schema of '{name}' is not a JSON object", nameof(tool));
    }
}
