using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The project memory survives beyond one call: a note written by <c>remember</c> is read back by a fresh
/// <c>project_memory</c> instance from the same workspace, its name is a confined slug, and it stays within its
/// ceilings. The writer is destructive and the reader is not.
/// </summary>
public sealed class ProjectMemoryToolTests
{
    [Fact]
    public async Task AWriteIsReadBackByAFreshReader()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var write = await new RememberTool().ExecuteAsync(
            ToolTestWorkspace.Call("remember", ToolTestWorkspace.Args(new
            {
                name = "Testing conventions",
                body = "Tests are xUnit with fluent assertions.",
            })),
            sandbox.Context,
            TestContext.Current.CancellationToken);
        Assert.False(write.IsError, write.Content);

        var read = await new ProjectMemoryTool().ExecuteAsync(
            ToolTestWorkspace.Call("project_memory", ToolTestWorkspace.Args(new { op = "get", name = "testing-conventions" })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(read.IsError, read.Content);
        Assert.Contains("fluent assertions", read.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListNamesEveryNote()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var remember = new RememberTool();
        await remember.ExecuteAsync(
            ToolTestWorkspace.Call("remember", ToolTestWorkspace.Args(new { name = "architecture", body = "one module." })),
            sandbox.Context,
            TestContext.Current.CancellationToken);
        await remember.ExecuteAsync(
            ToolTestWorkspace.Call("remember", ToolTestWorkspace.Args(new { name = "frozen", body = "do not touch." })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        var list = await new ProjectMemoryTool().ExecuteAsync(
            ToolTestWorkspace.Call("project_memory", ToolTestWorkspace.Args(new { op = "list" })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(list.IsError, list.Content);
        Assert.Contains("architecture", list.Content, StringComparison.Ordinal);
        Assert.Contains("frozen", list.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadSlugIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var write = await new RememberTool().ExecuteAsync(
            ToolTestWorkspace.Call("remember", ToolTestWorkspace.Args(new { name = "../escape", body = "x" })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(write.IsError, write.Content);
    }

    [Fact]
    public async Task AnOversizedNoteIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var body = new string('x', ProjectMemoryStore.MaxNoteBytes + 1);

        var write = await new RememberTool().ExecuteAsync(
            ToolTestWorkspace.Call("remember", ToolTestWorkspace.Args(new { name = "big", body })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(write.IsError, write.Content);
    }

    [Fact]
    public async Task ReadingAMissingNoteIsAFailure()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var read = await new ProjectMemoryTool().ExecuteAsync(
            ToolTestWorkspace.Call("project_memory", ToolTestWorkspace.Args(new { op = "get", name = "absent" })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(read.IsError, read.Content);
    }

    [Fact]
    public void TheReaderIsReadOnlyAndTheWriterIsDestructive()
    {
        Assert.Equal(ToolSafety.ReadOnly, new ProjectMemoryTool().Safety);
        Assert.Equal(ToolSafety.Destructive, new RememberTool().Safety);
    }
}
