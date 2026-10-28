using SharpCoreAgent.Domain.Errors;
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

    [Fact]
    public async Task LineEndingsAreForgivenAndTheFileKeepsItsOwn()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var path = sandbox.Write("a.txt", "one\r\ntwo\r\nthree\r\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new { path = "a.txt", find = "two\n", replace = "TWO\n" })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("line endings match", result.Content, StringComparison.Ordinal);
        Assert.Equal(
            "one\r\nTWO\r\nthree\r\n",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndentationIsForgivenAndTheReplacementTakesTheFilesIndentation()
    {
        using var sandbox = ToolTestWorkspace.Create();
        const string Before = "class A\n{\n    void M()\n    {\n        int x = 1;\n    }\n}\n";
        var path = sandbox.Write("a.cs", Before);

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new
                {
                    path = "a.cs",
                    find = "void M()\n{\n    int x = 1;\n}\n",
                    replace = "void M()\n{\n    int x = 2;\n}\n",
                })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("indentation match", result.Content, StringComparison.Ordinal);
        Assert.Equal(
            "class A\n{\n    void M()\n    {\n        int x = 2;\n    }\n}\n",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeveralFragmentsInOneCallAreAllApplied()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var path = sandbox.Write("a.txt", "a\nb\nc\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new
                {
                    path = "a.txt",
                    edits = new[] { new { find = "a", replace = "A" }, new { find = "c", replace = "C" } },
                })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("replaced 2 fragments (2 occurrence(s))", result.Content, StringComparison.Ordinal);
        Assert.Equal("A\nb\nC\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task AFragmentThatCannotBeMatchedLeavesTheWholeCallUnwritten()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var path = sandbox.Write("a.txt", "a\nb\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new
                {
                    path = "a.txt",
                    edits = new[] { new { find = "a", replace = "A" }, new { find = "zzz", replace = "Z" } },
                })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("block 2 of 2", result.Content, StringComparison.Ordinal);
        Assert.Contains("does not appear", result.Content, StringComparison.Ordinal);
        Assert.Equal("a\nb\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AmbiguityInOneFragmentRefusesTheWholeCall()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var path = sandbox.Write("a.txt", "x\nx\ny\n");

        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new
                {
                    path = "a.txt",
                    edits = new[] { new { find = "y", replace = "Y" }, new { find = "x", replace = "z" } },
                })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("block 2 of 2", result.Content, StringComparison.Ordinal);
        Assert.Contains("appears 2 times", result.Content, StringComparison.Ordinal);
        Assert.Equal("x\nx\ny\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheTwoFormsCannotBeMixed()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "a\n");

        await Assert.ThrowsAsync<ToolArgumentException>(() => new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new
                {
                    path = "a.txt",
                    find = "a",
                    replace = "b",
                    edits = new[] { new { find = "a", replace = "b" } },
                })),
            sandbox.Context,
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ThePreviewShowsEveryFragmentAgainstItsOwnIntermediateState()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "a\nb\nc\n");

        var preview = await new ReplaceInFileTool().DescribeChangeAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new
                {
                    path = "a.txt",
                    edits = new[] { new { find = "a", replace = "A" }, new { find = "c", replace = "C" } },
                })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.Contains("# block 1/2 (exact)", preview, StringComparison.Ordinal);
        Assert.Contains("# block 2/2 (exact)", preview, StringComparison.Ordinal);
        Assert.Contains("-a\n", preview, StringComparison.Ordinal);
        Assert.Contains("+A\n", preview, StringComparison.Ordinal);
        Assert.Contains("-c\n", preview, StringComparison.Ordinal);
        Assert.Contains("+C\n", preview, StringComparison.Ordinal);
        Assert.Equal(
            "a\nb\nc\n",
            await File.ReadAllTextAsync(sandbox.FullPath("a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFuzzyPreviewSaysWhichRungAcceptedTheMatch()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.cs", "class A\n{\n    void M()\n    {\n    }\n}\n");

        var preview = await new ReplaceInFileTool().DescribeChangeAsync(
            ToolTestWorkspace.Call(
                ReplaceInFileTool.Name,
                ToolTestWorkspace.Args(new
                {
                    path = "a.cs",
                    find = "void M()\n{\n}\n",
                    replace = "void M()\n{\n    return;\n}\n",
                })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.StartsWith("# match: indentation\n", preview, StringComparison.Ordinal);
    }
}

