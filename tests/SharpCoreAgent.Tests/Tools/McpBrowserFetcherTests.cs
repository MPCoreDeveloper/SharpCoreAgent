using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.Web;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The browser path must not claim to be available merely because some MCP server is running.
/// </summary>
public sealed class McpBrowserFetcherTests
{
    [Fact]
    public async Task AHostWithoutABrowserToolIsUnavailable()
    {
        using var host = new McpToolHost([], new McpClientOptions());
        var fetcher = new McpBrowserFetcher(host);
        using var sandbox = ToolTestWorkspace.Create();

        Assert.False(fetcher.IsAvailable);

        var result = await fetcher.FetchAsync(
            new Uri("https://example.com/"),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("browser navigation tool", result.Text, StringComparison.Ordinal);
    }
}
