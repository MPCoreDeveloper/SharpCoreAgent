using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// A tool name is what the model calls and what the developer reads in a transcript, so the folding rule is part
/// of the contract: one prefix per server, one name per tool, and a collision that is renamed rather than hidden.
/// </summary>
public sealed class McpToolNameTests
{
    [Theory]
    [InlineData("my::server", "my_server")]
    [InlineData("get__x", "get_x")]
    [InlineData("  padded  ", "padded")]
    [InlineData("Already_Fine", "already_fine")]
    [InlineData("---", "")]
    [InlineData("", "")]
    public void FoldingCollapsesRunsAndTrimsTheEnds(string input, string expected) =>
        Assert.Equal(expected, McpToolName.Fold(input));

    [Fact]
    public void ANameIsCappedAtTheLengthAProviderAccepts()
    {
        var name = McpToolName.ForTool("npx", new string('a', 100));
        Assert.Equal(McpToolName.MaxLength, name.Length);
    }

    [Fact]
    public void ASecondServerWithTheSameStemKeepsItsOwnPrefix()
    {
        Assert.Equal("npx", McpToolName.ForServer("npx", 1));
        Assert.Equal("npx_2", McpToolName.ForServer("npx", 2));
        Assert.NotEqual(
            McpToolName.ForTool("npx", "search"),
            McpToolName.ForTool(McpToolName.ForServer("npx", 2), "search"));
    }

    [Fact]
    public void AToolNameIsTheServerPrefixAndTheToolName()
    {
        Assert.Equal("npx_search", McpToolName.ForTool("npx", "search"));
        Assert.Equal("npx_tool", McpToolName.ForTool("npx", null));
        Assert.Equal("npx_read_file", McpToolName.ForTool("npx", "read.file"));
    }

    [Fact]
    public void ARepeatedToolNameIsSuffixedInsideTheCap()
    {
        var repeated = McpToolName.WithSuffix("npx_search", 2);
        var capped = McpToolName.WithSuffix(new string('a', McpToolName.MaxLength), 3);

        Assert.Equal("npx_search_2", repeated);
        Assert.True(capped.Length <= McpToolName.MaxLength);
        Assert.EndsWith("_3", capped, StringComparison.Ordinal);
        Assert.NotEqual(new string('a', McpToolName.MaxLength), capped);
    }
}
