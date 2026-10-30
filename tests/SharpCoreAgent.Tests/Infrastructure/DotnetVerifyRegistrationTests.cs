using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The gate is only honest if the tool it waits for is there, so these tests assert the container rather than the
/// gate: the verifier is registered in a build that asked for nothing, and the prompt that tells the model to call
/// it cannot name a tool this container does not have.
/// </summary>
public sealed class DotnetVerifyRegistrationTests
{
    [Fact]
    public void TheVerifierIsRegisteredInABuildThatConfiguredNothing()
    {
        using var provider = Build(new SharpCoreAgentOptions { IndexEnabled = false });

        var registry = provider.GetRequiredService<IToolRegistry>();
        var tool = registry.Find(ToolNames.DotnetVerify);

        Assert.NotNull(tool);
        Assert.Equal(ToolSafety.Destructive, tool.Safety);
        Assert.Contains(registry.Definitions, definition => definition.Name == ToolNames.DotnetVerify);
    }

    [Fact]
    public void ThePromptAndTheRegistryAgreeAboutTheVerifier() =>
        Assert.Contains(
            ToolNames.DotnetVerify,
            new SharpCoreAgentOptions().ToAgentOptions().SystemPrompt,
            StringComparison.Ordinal);

    private static ServiceProvider Build(SharpCoreAgentOptions options) =>
        new ServiceCollection().AddSharpCoreAgent(options).BuildServiceProvider();
}
