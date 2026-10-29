using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Web;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The strongest promise this round makes is structural: without <c>--web</c> there is no fetch tool to call, not a
/// fetch tool that decides to refuse. These tests assert the container, not the tool.
/// </summary>
public sealed class WebToolRegistrationTests
{
    [Fact]
    public void TheFetchToolIsAbsentUnlessTheNetworkWasAskedFor()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        Assert.DoesNotContain(
            "fetch_url",
            provider.GetRequiredService<IToolRegistry>().Definitions.Select(definition => definition.Name));
    }

    [Fact]
    public void TheFlagRegistersTheToolItsCeilingsAndItsNamedHosts()
    {
        using var provider = Build(new SharpCoreAgentOptions
        {
            IndexEnabled = false,
            WebAccessEnabled = true,
            WebAllowedHosts = ["localhost"],
        });

        var registry = provider.GetRequiredService<IToolRegistry>();
        var tool = registry.Find("fetch_url");

        Assert.NotNull(tool);
        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Contains(registry.Definitions, definition => definition.Name == "fetch_url");
        Assert.Equal(["localhost"], provider.GetRequiredService<WebFetchOptions>().AllowedHosts);
    }

    [Fact]
    public void TheNetworkIsNotInThePromptOfABuildThatDidNotAskForIt()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        Assert.DoesNotContain(
            "fetch_url",
            provider.GetRequiredService<AgentOptions>().SystemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheFetchClientIsItsOwnAndOwnsItsTimeout()
    {
        // The client the tool asks for by name must exist and must not carry a second ceiling: the tool's linked
        // cancellation source is the only budget, and a default 100-second HttpClient timeout underneath it would
        // surface as the wrong failure.
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false, WebAccessEnabled = true });

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(WebFetchOptions.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task TheContainersFetchClientJudgesTheAddressAgainAtConnectTime()
    {
        // The container is where the guard can quietly disappear: a handler built here without a connect callback would
        // leave the rebinding window open while every tool-level test still passed. This drives the client the
        // container built, with no tool in the way, and watches it refuse the address the socket was about to use.
        var services = new ServiceCollection();
        services.AddSingleton<IWebAddressResolver>(new LoopbackResolver());
        using var provider = services.AddSharpCoreAgent(new SharpCoreAgentOptions { IndexEnabled = false, WebAccessEnabled = true })
            .BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(WebFetchOptions.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://rebind.test/");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("refused at connect time", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("loopback", exception.ToString(), StringComparison.Ordinal);
    }

    private static ServiceProvider Build(SharpCoreAgentOptions options) =>
        new ServiceCollection().AddSharpCoreAgent(options).BuildServiceProvider();

    private sealed class LoopbackResolver : IWebAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]);
    }
}
