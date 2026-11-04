using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>The ReadOnly Git tools are reachable in a build that configured nothing and absent behind <c>--no-git</c>.</summary>
public sealed class GitToolRegistrationTests
{
    private static readonly string[] Tools = ["git_status", "git_diff"];

    [Fact]
    public void GitEvidenceIsReadOnlyAndRegisteredByDefault()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        var registry = provider.GetRequiredService<IToolRegistry>();
        Assert.All(
            Tools,
            name =>
            {
                var tool = registry.Find(name);
                Assert.NotNull(tool);
                Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
                Assert.Contains(registry.Definitions, definition => definition.Name == name);
            });
    }

    [Fact]
    public void TheOptOutRemovesBothToolsAndTheirRule()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false, GitToolsEnabled = false });

        var registry = provider.GetRequiredService<IToolRegistry>();
        Assert.All(Tools, name => Assert.Null(registry.Find(name)));
        Assert.DoesNotContain("git_status", provider.GetRequiredService<AgentOptions>().SystemPrompt, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(SharpCoreAgentOptions options) =>
        new ServiceCollection().AddSharpCoreAgent(options).BuildServiceProvider();
}
