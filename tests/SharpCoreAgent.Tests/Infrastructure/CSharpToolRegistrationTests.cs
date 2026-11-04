using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Code;
using SharpCoreAgent.Languages.CSharp;
using SharpCoreAgent.Languages.FSharp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>C# is the default code-intelligence adapter; F# adds a second adapter to the same three tools.</summary>
public sealed class CSharpToolRegistrationTests
{
    private static readonly string[] Tools = ["file_diagnostics", "uses_at", "signature_at"];

    [Fact]
    public void CSharpRegistersTheThreeToolsAndOneRouterByDefault()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        var registry = provider.GetRequiredService<IToolRegistry>();
        Assert.All(Tools, name => Assert.Equal(ToolSafety.ReadOnly, registry.Find(name)?.Safety));
        Assert.IsType<LanguageRouter>(provider.GetRequiredService<ICodeDiagnostics>());
        Assert.IsType<CSharpCodeIntelligence>(Assert.Single(provider.GetServices<ILanguageAdapter>()));
    }

    [Fact]
    public void TheCSharpRuleShipsWithItsToolsAndNotWithoutThem()
    {
        using var withTools = Build(new SharpCoreAgentOptions { IndexEnabled = false });
        using var withoutTools = Build(new SharpCoreAgentOptions { IndexEnabled = false, CSharpToolsEnabled = false });

        var enriched = withTools.GetRequiredService<AgentOptions>().SystemPrompt;
        var plain = withoutTools.GetRequiredService<AgentOptions>().SystemPrompt;

        Assert.EndsWith(PromptLibrary.CSharpRule, enriched, StringComparison.Ordinal);
        Assert.All(Tools, name => Assert.Contains(name, enriched, StringComparison.Ordinal));
        Assert.DoesNotContain("file_diagnostics", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void FSharpAddsAnAdapterWithoutDuplicatingTheTools()
    {
        using var provider = Build(new SharpCoreAgentOptions
        {
            IndexEnabled = false,
            FSharpToolsEnabled = true,
        });

        var adapters = provider.GetServices<ILanguageAdapter>().ToArray();
        var tools = provider.GetServices<ITool>().Where(tool => Tools.Contains(tool.Definition.Name)).ToArray();

        Assert.Equal(2, adapters.Length);
        Assert.Contains(adapters, adapter => adapter is CSharpCodeIntelligence);
        Assert.Contains(adapters, adapter => adapter is FsCodeIntelligence);
        Assert.Equal(3, tools.Length);
    }

    [Fact]
    public void NeitherAdapterMeansNoRouterAndNoTools()
    {
        using var provider = Build(new SharpCoreAgentOptions
        {
            IndexEnabled = false,
            CSharpToolsEnabled = false,
        });

        var registry = provider.GetRequiredService<IToolRegistry>();
        Assert.All(Tools, name => Assert.Null(registry.Find(name)));
        Assert.Null(provider.GetService<ICodeDiagnostics>());
        Assert.Empty(provider.GetServices<ILanguageAdapter>());
    }

    private static ServiceProvider Build(SharpCoreAgentOptions options) =>
        new ServiceCollection().AddSharpCoreAgent(options).BuildServiceProvider();
}
