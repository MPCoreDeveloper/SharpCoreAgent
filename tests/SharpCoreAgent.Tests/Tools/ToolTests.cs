using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The contract every tool owes the loop: refuse escapes, validate before touching anything, bound the
/// result and report what changed.
/// </summary>
public sealed class ToolTests
{
    [Fact]
    public async Task ReadFile_RefusesPathOutsideWorkspace()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var outside = Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "outside.txt");
        File.WriteAllText(outside, "secret");

        var result = await new ReadFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("read_file", """{"path":"../outside.txt"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsRefused);
        Assert.True(result.IsError);
        Assert.Contains("outside the workspace root", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_RefusesAbsolutePathOutsideWorkspace()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var outside = Path.Combine(Path.GetTempPath(), "sharpcoreagent-outside.txt");

        var result = await new ReadFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("read_file", ToolTestWorkspace.Args(new { path = outside })),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsRefused);
    }

    [Fact]
    public async Task ReadFile_NumbersLines()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("notes.txt", "alpha\nbeta");

        var result = await new ReadFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("read_file", """{"path":"notes.txt"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("   1│alpha\n   2│beta", result.Content);
        Assert.False(result.IsTruncated);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "notes.txt"), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task ReadFile_HonoursStartLineAndFlagsTruncation()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("lines.txt", string.Join('\n', Enumerable.Range(1, 10).Select(index => $"line{index}")));

        var result = await new ReadFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("read_file", """{"path":"lines.txt","start_line":2,"line_count":3}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(result.IsTruncated);
        Assert.StartsWith("   2│line2\n   3│line3\n   4│line4", result.Content, StringComparison.Ordinal);
        Assert.Contains(
            "[truncated: showed lines 2-4; continue with start_line=5]",
            result.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_RejectsBinaryFile()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("image.bin", [(byte)'P', 0, (byte)'N', (byte)'G']);

        var result = await new ReadFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("read_file", """{"path":"image.bin"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("binary", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_MissingFileIsAnErrorResult()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new ReadFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("read_file", """{"path":"nope.txt"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(result.IsRefused);
        Assert.Contains("no file at 'nope.txt'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_RejectsMissingPathArgument()
    {
        using var sandbox = ToolTestWorkspace.Create();

        await Assert.ThrowsAsync<ToolArgumentException>(async () =>
            await new ReadFileTool().ExecuteAsync(
                ToolTestWorkspace.Call("read_file", "{}"),
                sandbox.Context,
                CancellationToken.None));
    }

    [Fact]
    public async Task WriteFile_CreatesFileAndReportsCreatedEffect()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new WriteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("write_file", """{"path":"src/app.txt","content":"hello"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("wrote 5 bytes to src/app.txt (created)", result.Content);
        Assert.Equal(new ToolEffect(ToolEffectKind.Created, "src/app.txt"), Assert.Single(result.Effects));

        var bytes = File.ReadAllBytes(sandbox.FullPath("src/app.txt"));
        Assert.Equal("hello"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task WriteFile_OverwritesAndReportsModifiedEffect()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("app.txt", "old");

        var result = await new WriteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("write_file", """{"path":"app.txt","content":"new"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("(overwritten)", result.Content, StringComparison.Ordinal);
        Assert.Equal(new ToolEffect(ToolEffectKind.Modified, "app.txt"), Assert.Single(result.Effects));
        Assert.Equal("new", File.ReadAllText(sandbox.FullPath("app.txt")));
    }

    [Fact]
    public async Task WriteFile_RefusesToOverwriteWhenToldNotTo()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("app.txt", "keep me");

        var result = await new WriteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("write_file", """{"path":"app.txt","content":"replace","overwrite":false}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Effects);
        Assert.Equal("keep me", File.ReadAllText(sandbox.FullPath("app.txt")));
    }

    [Fact]
    public async Task DeleteFile_RemovesFileAndReportsDeletedEffect()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("gone.txt", "x");

        var result = await new DeleteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("delete_file", """{"path":"gone.txt"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("deleted gone.txt", result.Content);
        Assert.Equal(new ToolEffect(ToolEffectKind.Deleted, "gone.txt"), Assert.Single(result.Effects));
        Assert.False(File.Exists(sandbox.FullPath("gone.txt")));
    }

    [Fact]
    public async Task DeleteFile_RefusesDirectoriesAndMissingFiles()
    {
        using var sandbox = ToolTestWorkspace.Create();
        Directory.CreateDirectory(sandbox.FullPath("folder"));
        var tool = new DeleteFileTool();

        var directory = await tool.ExecuteAsync(
            ToolTestWorkspace.Call("delete_file", """{"path":"folder"}"""),
            sandbox.Context,
            CancellationToken.None);
        var missing = await tool.ExecuteAsync(
            ToolTestWorkspace.Call("delete_file", """{"path":"missing.txt"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(directory.IsError);
        Assert.True(Directory.Exists(sandbox.FullPath("folder")));
        Assert.True(missing.IsError);
        Assert.Empty(missing.Effects);
    }

    [Fact]
    public async Task ReadFile_IsIdempotent()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("stable.txt", "same");
        var tool = new ReadFileTool();
        var call = ToolTestWorkspace.Call("read_file", """{"path":"stable.txt"}""");

        var first = await tool.ExecuteAsync(call, sandbox.Context, CancellationToken.None);
        var second = await tool.ExecuteAsync(call, sandbox.Context, CancellationToken.None);

        Assert.Equal(first.Content, second.Content);
    }

    [Fact]
    public async Task ListDirectory_MarksDirectoriesAndDefaultsToShallowListing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "a");
        sandbox.Write("pkg/inner.txt", "inner");

        var shallow = await new ListDirectoryTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_directory", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(shallow.IsError);
        Assert.Equal("f a.txt\nd pkg/", shallow.Content);

        var deep = await new ListDirectoryTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_directory", """{"recursive":true}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.Contains("d pkg/", deep.Content, StringComparison.Ordinal);
        Assert.Contains("f pkg/inner.txt", deep.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListDirectory_SkipsIgnoredNames()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("node_modules/pkg/index.js", "x");
        sandbox.Write("src/main.cs", "x");

        var result = await new ListDirectoryTool().ExecuteAsync(
            ToolTestWorkspace.Call("list_directory", """{"recursive":true}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.Contains("f src/main.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", result.Content, StringComparison.Ordinal);
        Assert.Equal(
            [".git", "node_modules", "bin", "obj", ".vs", ".sharpcoreagent"],
            IgnoredPaths.DirectoryNames);
    }
}
