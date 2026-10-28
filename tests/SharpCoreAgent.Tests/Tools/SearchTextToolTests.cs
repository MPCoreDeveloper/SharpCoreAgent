using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// <c>search_text</c> is the tool the model leans on to find its bearings, so the tests pin the result
/// shape (<c>path:line: text</c>), the noise it skips and the budget it respects.
/// </summary>
public sealed class SearchTextToolTests
{
    [Fact]
    public async Task FindsMatchesCaseInsensitivelyWithPathAndLine()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/app.cs", "line one\n// Needle here\n");

        var result = await new SearchTextTool().ExecuteAsync(
            ToolTestWorkspace.Call("search_text", """{"pattern":"needle"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("src/app.cs:2: // Needle here", result.Content);
        Assert.False(result.IsTruncated);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "."), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task ReportsNoMatchesWithoutFailing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/app.cs", "nothing to see");

        var result = await new SearchTextTool().ExecuteAsync(
            ToolTestWorkspace.Call("search_text", """{"pattern":"absent"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("no matches for 'absent'", result.Content);
    }

    [Fact]
    public async Task AppliesGlobFilter()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/app.cs", "needle");
        sandbox.Write("docs/guide.md", "needle");

        var result = await new SearchTextTool().ExecuteAsync(
            ToolTestWorkspace.Call("search_text", """{"pattern":"needle","glob":"*.cs"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.Contains("src/app.cs:1: needle", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("guide.md", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipsIgnoredDirectoriesAndBinaryFiles()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("node_modules/pkg/index.js", "needle");
        sandbox.Write("obj/generated.cs", "needle");
        sandbox.WriteBytes("assets/logo.bin", [(byte)'n', (byte)'e', 0, (byte)'e']);
        sandbox.Write("src/app.cs", "needle");

        var result = await new SearchTextTool().ExecuteAsync(
            ToolTestWorkspace.Call("search_text", """{"pattern":"needle"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.Equal("src/app.cs:1: needle", result.Content);
    }

    [Fact]
    public async Task StopsAtMaxMatchesAndFlagsTruncation()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "hit\nhit\nhit\n");

        var result = await new SearchTextTool().ExecuteAsync(
            ToolTestWorkspace.Call("search_text", """{"pattern":"hit","max_matches":2}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsTruncated);
        Assert.EndsWith(TextLimits.TruncatedMarker, result.Content, StringComparison.Ordinal);
        Assert.Equal(2, result.Content.Split('\n').Length - 1);
    }

    [Fact]
    public async Task SearchesAnAttachmentWhenGivenItsAbsolutePath()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var attachment = sandbox.AddAttachment("shared");
        ToolTestWorkspace.WriteUnder(attachment, "src/Order.cs", "// needle in the attachment\n");
        sandbox.Write("src/app.cs", "// needle in the workspace\n");

        var result = await new SearchTextTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                "search_text",
                ToolTestWorkspace.Args(new { pattern = "needle", path = attachment })),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("src/Order.cs:1: // needle in the attachment", result.Content);

        // The hit is reported relative to the tree that answered, and the effect says which scope was searched.
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "."), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task ASingleFilePathSearchesThatFileOnly()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "needle\n");
        sandbox.Write("b.txt", "needle\n");

        var result = await new SearchTextTool().ExecuteAsync(
            ToolTestWorkspace.Call("search_text", """{"pattern":"needle","path":"a.txt"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.Equal("a.txt:1: needle", result.Content);
    }
}
