using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The structural promise of this round: without <c>--mcp</c> there is no client, no host and no tool that could
/// start a program, and the prompt says nothing about servers. These tests assert the container, not a flag.
/// </summary>
public sealed class McpRegistrationTests
{
    [Fact]
    public void NoHostIsRegisteredWithoutAServer()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        Assert.Null(provider.GetService<McpToolHost>());
        Assert.DoesNotContain(
            provider.GetRequiredService<SharpCoreAgent.Application.IToolRegistry>().Definitions,
            definition => definition.Name.Contains('_', StringComparison.Ordinal) &&
                          definition.Name.StartsWith("npx", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHostExistsAndKnowsEveryServerItWasGiven()
    {
        using var provider = Build(new SharpCoreAgentOptions
        {
            IndexEnabled = false,
            McpServers = ["npx -y one", "npx -y two", "uvx three"],
        });

        var host = provider.GetRequiredService<McpToolHost>();

        // Constructing the host starts nothing: the command lines are recorded and the children are spawned on
        // the first Start, which is why a registration test costs no process.
        Assert.Equal(["npx", "npx_2", "uvx"], host.Servers.Select(server => server.Name));
        Assert.Empty(host.Tools);
    }

    [Fact]
    public void ThePromptGainsTheServerRuleOnlyWhenOneWasNamed()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false, McpServers = ["npx -y one"] });
        using var without = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        var prompt = provider.GetRequiredService<SharpCoreAgent.Application.AgentOptions>().SystemPrompt;
        var quiet = without.GetRequiredService<SharpCoreAgent.Application.AgentOptions>().SystemPrompt;

        Assert.Contains("Using the MCP servers", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Using the MCP servers", quiet, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSamplingPolicyReachesTheClientOptions()
    {
        using var provider = Build(new SharpCoreAgentOptions
        {
            IndexEnabled = false,
            McpServers = ["npx -y one"],
            McpSampling = McpSamplingPolicy.First,
        });

        Assert.Equal(McpSamplingPolicy.First, provider.GetRequiredService<McpClientOptions>().SamplingPolicy);
    }

    [Fact]
    public void ARemoteUrlRegistersAHostWithoutStartingIt()
    {
        using var provider = Build(new SharpCoreAgentOptions
        {
            IndexEnabled = false,
            McpUrls = ["https://mcp.example/rpc"],
        });

        var host = provider.GetRequiredService<McpToolHost>();
        var server = Assert.Single(host.Servers);
        Assert.Equal("https://mcp.example/rpc", server.CommandLine);
        Assert.Contains("mcp", server.Name, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(SharpCoreAgentOptions options) =>
        new ServiceCollection().AddSharpCoreAgent(options).BuildServiceProvider();
}
