using System.Text.Json;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Web;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The browser path: calls the Playwright MCP server's <c>browser_navigate</c> tool.
/// </summary>
/// <remarks>
/// The adapter deliberately does not start a browser itself. It uses the same host, process and tool set the
/// <c>--browser</c> flag started, so there is one browser implementation and one process tree to dispose.
/// </remarks>
public sealed class McpBrowserFetcher : IBrowserFetcher
{
    private readonly McpToolHost _host;

    /// <summary>Creates the adapter over an already registered host.</summary>
    /// <param name="host">The MCP host, which owns the started Playwright server.</param>
    public McpBrowserFetcher(McpToolHost host) =>
        _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc />
    public bool IsAvailable => _host.Servers.Count > 0;

    /// <inheritdoc />
    public async ValueTask<BrowserFetchResult> FetchAsync(
        Uri uri,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var tools = _host.Started().Tools;
            var navigate = tools.FirstOrDefault(tool =>
                tool.Definition.Name.EndsWith("_browser_navigate", StringComparison.Ordinal));
            if (navigate is null)
            {
                return BrowserFetchResult.Failure(
                    $"no browser navigation tool is registered for {uri.AbsoluteUri}; start the session with --browser.");
            }

            var result = await navigate.ExecuteAsync(
                new ToolCall(
                    "browser_navigate",
                    navigate.Definition.Name,
                    JsonSerializer.Serialize(new { url = uri.AbsoluteUri })),
                context,
                cancellationToken).ConfigureAwait(false);

            return result.IsError || result.IsRefused
                ? BrowserFetchResult.Failure(result.Content)
                : BrowserFetchResult.Success(result.Content);
        }
        catch (McpStartupException ex)
        {
            return BrowserFetchResult.Failure($"the browser server could not start: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return BrowserFetchResult.Failure($"the browser navigation failed: {ex.Message}");
        }
    }
}
