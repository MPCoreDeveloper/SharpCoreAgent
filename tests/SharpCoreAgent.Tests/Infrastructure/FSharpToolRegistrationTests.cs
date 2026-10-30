using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Code;
using SharpCoreAgent.Languages.FSharp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The structural promise of the round: without the flag there is no tool to call and no checker in the process.
/// </summary>
/// <remarks>
/// These tests assert the container, not a tool, for the same reason <see cref="WebToolRegistrationTests"/> does: a
/// capability that exists and decides to refuse is a different promise from one that is not there.
/// </remarks>
public sealed class FSharpToolRegistrationTests
{
    private static readonly string[] Tools = ["file_diagnostics", "uses_at", "signature_at"];

    [Fact]
    public void TheToolsAreAbsentUnlessTheFlagIsSet()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        var registry = provider.GetRequiredService<IToolRegistry>();

        Assert.All(Tools, name => Assert.Null(registry.Find(name)));
        Assert.Null(provider.GetService<ICodeDiagnostics>());
        Assert.Null(provider.GetService<ILanguageAdapter>());
    }

    [Fact]
    public void TheFlagRegistersTheThreeToolsAsReadOnly()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false, FSharpToolsEnabled = true });

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
    public void TheThreePortsAreOneRouterOverOneAdapter()
    {
        // The tools are handed ports, so this is where the three questions are proved to reach one instance: a second
        // router would mean two adapters, two caches and two answers about one project.
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false, FSharpToolsEnabled = true });

        var router = provider.GetRequiredService<ICodeDiagnostics>();

        Assert.IsType<LanguageRouter>(router);
        Assert.Same(router, provider.GetRequiredService<ISymbolUses>());
        Assert.Same(router, provider.GetRequiredService<ISignatureHelp>());
        Assert.IsType<FsCodeIntelligence>(provider.GetRequiredService<ILanguageAdapter>());
        Assert.Single(provider.GetServices<ILanguageAdapter>());
    }

    [Fact]
    public void TheRuleShipsOnlyWithTheToolsItDescribes()
    {
        using var withoutTools = Build(new SharpCoreAgentOptions { IndexEnabled = false });
        using var withTools = Build(new SharpCoreAgentOptions { IndexEnabled = false, FSharpToolsEnabled = true });

        var plain = withoutTools.GetRequiredService<AgentOptions>().SystemPrompt;
        var enriched = withTools.GetRequiredService<AgentOptions>().SystemPrompt;

        Assert.All(Tools, name => Assert.DoesNotContain(name, plain, StringComparison.Ordinal));
        Assert.All(Tools, name => Assert.Contains(name, enriched, StringComparison.Ordinal));
        Assert.EndsWith(PromptLibrary.FSharpRule, enriched, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(SharpCoreAgentOptions options) =>
        new ServiceCollection().AddSharpCoreAgent(options).BuildServiceProvider();
}
