using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// Fragment editing is the safety property the whole edit path rests on: the file must come out
/// byte-identical except for the replaced text, and an ambiguous match must refuse rather than guess.
/// </summary>
public sealed class ReplaceInFileToolTests
{
    [Fact]
    public async Task ReplacesOneOccurrenceAndKeepsEverythingElse()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/a.cs", "class A\n{\n    int x = 1;\n}\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(ReplaceInFileTool.Name, """{"path":"src/a.cs","find":"int x = 1;","replace":"int x = 2;"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("replaced 1 occurrence", result.Content, StringComparison.Ordinal);
        Assert.Equal(new ToolEffect(ToolEffectKind.Modified, "src/a.cs"), Assert.Single(result.Effects));
        Assert.Equal(
            "class A\n{\n    int x = 2;\n}\n",
            await File.ReadAllTextAsync(sandbox.FullPath("src/a.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AmbiguousMatchIsRefusedAndTheFileIsUntouched()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var path = sandbox.Write("a.txt", "x\ny\nx\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(ReplaceInFileTool.Name, """{"path":"a.txt","find":"x","replace":"z"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("appears 2 times", result.Content, StringComparison.Ordinal);
        Assert.Contains("all=true", result.Content, StringComparison.Ordinal);
        Assert.Equal("x\ny\nx\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AllTrueReplacesEveryOccurrence()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var path = sandbox.Write("a.txt", "x\ny\nx\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(ReplaceInFileTool.Name, """{"path":"a.txt","find":"x","replace":"z","all":true}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("z\ny\nz\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingTextFailsWithGuidance()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "alpha\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(ReplaceInFileTool.Name, """{"path":"a.txt","find":"beta","replace":"gamma"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("does not appear", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesToEditOutsideTheWorkspace()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(ReplaceInFileTool.Name, """{"path":"../outside.txt","find":"a","replace":"b"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsRefused);
    }

    [Fact]
    public async Task PreviewShowsTheChangeWithoutMakingIt()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "one\ntwo\nthree\n");

        var preview = await new ReplaceInFileTool().DescribeChangeAsync(
            ToolTestWorkspace.Call(ReplaceInFileTool.Name, """{"path":"a.txt","find":"two","replace":"TWO"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.Contains("@@ -2,1 +2,1 @@", preview, StringComparison.Ordinal);
        Assert.Contains("-two\n", preview, StringComparison.Ordinal);
        Assert.Contains("+TWO\n", preview, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\nthree\n", await File.ReadAllTextAsync(sandbox.FullPath("a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreviewIsNullWhenTheMatchIsMissing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "alpha\n");

        var preview = await new ReplaceInFileTool().DescribeChangeAsync(
            ToolTestWorkspace.Call(ReplaceInFileTool.Name, """{"path":"a.txt","find":"beta","replace":"gamma"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.Null(preview);
    }
}
