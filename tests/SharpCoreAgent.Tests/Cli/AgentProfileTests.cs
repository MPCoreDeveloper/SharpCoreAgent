using SharpCoreAgent.Cli;
using SharpCoreAgent.Infrastructure.McpServer;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The agent profile <c>ide agent</c> prints, held to the format as it was read rather than to how it looks.
/// </summary>
/// <remarks>
/// The format is somebody else's, so every case here quotes a documented fact: the frontmatter is YAML between two
/// <c>---</c> lines, <c>description</c> is required, an MCP tool is <c>&lt;server&gt;/&lt;tool&gt;</c>, the prompt is capped at
/// 30,000 characters, and the file's name is the name the picker shows when no <c>name</c> key is given.
/// </remarks>
public sealed class AgentProfileTests
{
    [Fact]
    public void TheFrontmatterOpensAndClosesOnItsOwnLines()
    {
        var lines = Lines(AgentProfile.Render());

        Assert.Equal("---", lines[0]);

        // Closed on the fourth line: description, tools, close. A fifth key would mean the round added one without a
        // paragraph in the plan saying which.
        Assert.Equal("---", lines[3]);
        Assert.Equal(2, lines.Count(line => line == "---"));
    }

    [Fact]
    public void TheFrontmatterCarriesADescriptionAndToolsAndNothingElse()
    {
        var lines = Lines(AgentProfile.Render());

        Assert.StartsWith("description: ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("tools: [", lines[2], StringComparison.Ordinal);

        // The four keys this round decided against, each for a documented reason: the file's name is the name, the
        // model belongs to the developer, unset target means both environments, and IDE agents ignore mcp-servers.
        Assert.DoesNotContain(lines[1..3], line => line.StartsWith("name:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines[1..3], line => line.StartsWith("model:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines[1..3], line => line.StartsWith("target:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines[1..3], line => line.Contains("mcp-servers", StringComparison.Ordinal));
    }

    [Fact]
    public void TheToolListIsThePublishedSetPrefixedAndInOrder()
    {
        // The one assertion that keeps the profile and the server from drifting: the profile lists what
        // McpServerCatalog publishes, in the contract's order, under the name both MCP files use.
        var expected = string.Join(
            ", ",
            McpServerCatalog.PublishedToolNames.Select(name => $"\"{McpClientSnippets.ServerName}/{name}\""));

        Assert.Contains($"tools: [{expected}]", AgentProfile.Render(), StringComparison.Ordinal);
        Assert.Equal(5, McpServerCatalog.PublishedToolNames.Count);
    }

    [Fact]
    public void TheBodyNamesEveryPublishedTool()
    {
        var profile = AgentProfile.Render();

        Assert.All(
            McpServerCatalog.PublishedToolNames,
            name => Assert.Contains($"`{McpClientSnippets.ServerName}/{name}`", profile, StringComparison.Ordinal));
    }

    [Fact]
    public void TheBodySaysTheToolsAreReadOnlyAndHowToReportAMissingIndex()
    {
        // Three sentences an agent repeats back to a developer: nothing here writes, a quote carries the line numbers the
        // tool returned, and a missing index is said rather than guessed around.
        var profile = AgentProfile.Render();

        Assert.Contains("read-only", profile, StringComparison.Ordinal);
        Assert.Contains("path:start-end", profile, StringComparison.Ordinal);
        Assert.Contains("`missing`", profile, StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent index", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkerIsTheLastLine()
    {
        var lines = Lines(AgentProfile.Render());

        Assert.Equal(AgentProfile.Marker, lines[^1]);
        Assert.Single(lines, line => line == AgentProfile.Marker);
    }

    [Fact]
    public void TheProfileIsWellUnderTheDocumentedPromptCap()
    {
        // The reference says "the prompt can be a maximum of 30,000 characters". This one is a page, and a test that
        // pinned the exact count would fail for a typo's worth of growth — so it pins the order of magnitude.
        Assert.True(AgentProfile.Render().Length < 5_000, "the profile grew past a page; the cap is 30,000");
    }

    [Fact]
    public void TheDocumentUsesOneLineEndingThroughout()
    {
        var profile = AgentProfile.Render();

        Assert.EndsWith(Environment.NewLine, profile, StringComparison.Ordinal);

        // Nothing left after the pairs are removed: no lone CR and no lone LF, so the file is the platform's throughout.
        var withoutPairs = profile.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', withoutPairs);
        Assert.DoesNotContain('\n', withoutPairs);
    }

    [Theory]
    [InlineData("---\ndescription: x\n---\n")]
    [InlineData("my own agent\n")]
    [InlineData("")]
    public void AFileWithoutTheMarkerIsNotOurs(string text) => Assert.False(AgentProfile.IsOurs(text));

    [Fact]
    public void AFileWithTheMarkerIsOursWhateverSurroundsIt()
    {
        var edited = $"---\ndescription: mine\n---\n\nsomebody's prompt\n{AgentProfile.Marker}\n";

        Assert.True(AgentProfile.IsOurs(edited));
    }

    [Fact]
    public void TheDestinationIsTheGitHubAgentsDirectoryOfTheWorkspace()
    {
        var root = Path.Combine("C:", "work", "repo");

        Assert.Equal(
            Path.Combine(root, ".github", "agents", "sharpcoreagent.agent.md"),
            AgentProfile.Destination(root));
    }

    private static string[] Lines(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];
}
