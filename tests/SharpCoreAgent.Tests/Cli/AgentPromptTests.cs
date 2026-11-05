using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>The prompt file is the agent body in the prompt picker's frontmatter.</summary>
public sealed class AgentPromptTests
{
    [Fact]
    public void ThePromptHasModeAndDescriptionAndItsMarker()
    {
        var prompt = AgentPrompt.Render();

        Assert.StartsWith("---\nmode: agent\ndescription: ", prompt.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent/search_code", prompt, StringComparison.Ordinal);
        Assert.EndsWith(AgentPrompt.Marker + "\n", prompt.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.EndsWith("sharpcoreagent.prompt.md", AgentPrompt.Destination("C:\\work"), StringComparison.Ordinal);
    }
}
