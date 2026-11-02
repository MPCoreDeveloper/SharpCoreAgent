namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// The five tool names this server publishes, stated once — as a contract rather than as a private list.
/// </summary>
/// <remarks>
/// <see cref="McpServerPublishedSet"/> still decides what is served: a name here that the registry does not hold, or
/// that is no longer read-only, answers nobody. But the *names* are public because they have a second reader outside
/// this assembly — an agent definition written into a developer's workspace lists exactly the tools the server answers,
/// and a copy in the CLI would be a copy that drifts the first time a tool is added. That is the failure mode `022`'s
/// D8 named for the MCP blocks, one step further out: an editor shows a missing tool at the moment somebody asks a
/// question, with nothing in either file to say which of the two is wrong.
/// </remarks>
public static class McpServerCatalog
{
    /// <summary>
    /// Gets the published names, in the order the contract documents them.
    /// </summary>
    /// <remarks>
    /// Ordinal order, like the registry's, so <c>tools/list</c> and an agent profile list the same five tools in the
    /// same order. Nothing outside <see cref="McpServerPublishedSet"/> may widen this: it is an allow-list, and the
    /// filter that intersects it with the registry is the requirement rather than an optimisation.
    /// </remarks>
    public static IReadOnlyList<string> PublishedToolNames { get; } =
    [
        Tools.CodeContextTool.Name,
        Tools.FindReferencesTool.Name,
        Tools.FindSymbolTool.Name,
        Tools.IndexStatusTool.Name,
        Tools.SearchCodeTool.Name,
    ];
}
