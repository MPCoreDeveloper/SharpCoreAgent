using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>The agent skill has its own directory and YAML name.</summary>
public sealed class AgentSkillTests
{
    [Fact]
    public void TheSkillHasNameDescriptionAndMarker()
    {
        var skill = AgentSkill.Render().Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.StartsWith("---\nname: sharpcoreagent\ndescription: ", skill, StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent/search_code", skill, StringComparison.Ordinal);
        Assert.EndsWith(AgentSkill.Marker + "\n", skill, StringComparison.Ordinal);
        Assert.EndsWith("SKILL.md", AgentSkill.Destination("C:\\work"), StringComparison.Ordinal);
    }
}
