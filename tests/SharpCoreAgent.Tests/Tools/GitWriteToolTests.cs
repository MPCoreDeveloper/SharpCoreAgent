using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The Git write tools record what the model proposes: fixed argv, confined paths, a validated branch name, and a
/// commit whose subject is exactly the message the developer saw at the approval prompt. No <c>run_shell</c>.
/// </summary>
public sealed class GitWriteToolTests
{
    [Fact]
    public async Task StageThenCommitRecordsTheProposedSubject()
    {
        using var sandbox = GitSandbox.Create();
        sandbox.Write("tracked.txt", "one\nchanged\n");

        var stage = await new GitStageTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_stage", ToolTestWorkspace.Args(new { paths = new[] { "tracked.txt" } })),
            sandbox.Context,
            CancellationToken.None);
        Assert.False(stage.IsError, stage.Content);

        var commit = await new GitCommitTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_commit", ToolTestWorkspace.Args(new { message = "update tracked" })),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(commit.IsError, commit.Content);
        Assert.Contains("committed", commit.Content, StringComparison.Ordinal);
        Assert.Equal("update tracked", sandbox.Run("log", "-1", "--pretty=%s").Trim());
    }

    [Fact]
    public void AllThreeWritesAreDestructive()
    {
        Assert.Equal(ToolSafety.Destructive, new GitStageTool().Safety);
        Assert.Equal(ToolSafety.Destructive, new GitCommitTool().Safety);
        Assert.Equal(ToolSafety.Destructive, new GitBranchTool().Safety);
    }

    [Fact]
    public async Task CommitWithoutAMessageIsAFailure()
    {
        using var sandbox = GitSandbox.Create();

        var commit = await new GitCommitTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_commit", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(commit.IsError, commit.Content);
    }

    [Fact]
    public async Task StagingAnEscapingPathIsRefused()
    {
        using var sandbox = GitSandbox.Create();

        var stage = await new GitStageTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_stage", ToolTestWorkspace.Args(new { paths = new[] { "../evil.txt" } })),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(stage.IsError, stage.Content);
        Assert.True(stage.IsRefused, stage.Content);
    }

    [Fact]
    public async Task BranchCreatesListsAndDeletes()
    {
        using var sandbox = GitSandbox.Create();
        var tool = new GitBranchTool();

        var create = await tool.ExecuteAsync(
            ToolTestWorkspace.Call("git_branch", ToolTestWorkspace.Args(new { op = "create", name = "feature/x" })),
            sandbox.Context,
            CancellationToken.None);
        Assert.False(create.IsError, create.Content);

        var list = await tool.ExecuteAsync(
            ToolTestWorkspace.Call("git_branch", ToolTestWorkspace.Args(new { op = "list" })),
            sandbox.Context,
            CancellationToken.None);
        Assert.Contains("feature/x", list.Content, StringComparison.Ordinal);

        var delete = await tool.ExecuteAsync(
            ToolTestWorkspace.Call("git_branch", ToolTestWorkspace.Args(new { op = "delete", name = "feature/x" })),
            sandbox.Context,
            CancellationToken.None);
        Assert.False(delete.IsError, delete.Content);
    }

    [Fact]
    public async Task BranchRejectsOptionLookingNamesBeforeGitRuns()
    {
        using var sandbox = GitSandbox.Create();

        var create = await new GitBranchTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_branch", ToolTestWorkspace.Args(new { op = "create", name = "-D" })),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(create.IsError, create.Content);
    }

    [Fact]
    public async Task DeletingTheCheckedOutBranchIsRefused()
    {
        using var sandbox = GitSandbox.Create();
        var current = sandbox.Run("rev-parse", "--abbrev-ref", "HEAD").Trim();

        var delete = await new GitBranchTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_branch", ToolTestWorkspace.Args(new { op = "delete", name = current })),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(delete.IsError, delete.Content);
    }

    [Fact]
    public async Task TheCommitPreviewNamesTheSubject()
    {
        using var sandbox = GitSandbox.Create();

        var preview = await new GitCommitTool().DescribeChangeAsync(
            ToolTestWorkspace.Call("git_commit", ToolTestWorkspace.Args(new { message = "tighten the build" })),
            sandbox.Context,
            CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Contains("tighten the build", preview, StringComparison.Ordinal);
    }
}
