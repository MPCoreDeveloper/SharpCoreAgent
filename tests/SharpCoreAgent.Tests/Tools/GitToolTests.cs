using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The Git tools are the review surface: they must show the same repository Git shows without accepting a command,
/// a revision expression or a path outside the workspace.
/// </summary>
public sealed class GitToolTests
{
    [Fact]
    public async Task StatusSeparatesTheIndexTheWorktreeAndUntrackedFiles()
    {
        using var sandbox = GitSandbox.Create();
        sandbox.Write("tracked.txt", "one\nchanged\n");
        sandbox.Write("staged.txt", "new\n");
        sandbox.Add("staged.txt");
        sandbox.Write("untracked.txt", "not added\n");
        var tool = new GitStatusTool();

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call("git_status", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Contains(" M tracked.txt", result.Content, StringComparison.Ordinal);
        Assert.Contains("A  staged.txt", result.Content, StringComparison.Ordinal);
        Assert.Contains("?? untracked.txt", result.Content, StringComparison.Ordinal);
        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Empty(result.Effects);
    }

    [Fact]
    public async Task StatusScopesItselfToTheRequestedPath()
    {
        using var sandbox = GitSandbox.Create();
        sandbox.Write("tracked.txt", "changed\n");
        sandbox.Write("other.txt", "new\n");

        var result = await new GitStatusTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_status", ToolTestWorkspace.Args(new { path = "tracked.txt" })),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("tracked.txt", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("other.txt", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStandaloneRenameIsReadableRatherThanTwoNulRecords()
    {
        using var sandbox = GitSandbox.Create();
        sandbox.Run("mv", "tracked.txt", "renamed.txt");

        var result = await new GitStatusTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_status", "{}"),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("R  tracked.txt -> renamed.txt", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', result.Content);
    }

    [Fact]
    public async Task DiffWorkingStagedAndHeadShowTheThreeComparisons()
    {
        using var sandbox = GitSandbox.Create("base\n");
        sandbox.Write("tracked.txt", "staged\n");
        sandbox.Add("tracked.txt");
        sandbox.Write("tracked.txt", "working\n");

        var working = await DiffAsync(sandbox, "working");
        var staged = await DiffAsync(sandbox, "staged");
        var head = await DiffAsync(sandbox, "head");

        Assert.Contains("-staged", working.Content, StringComparison.Ordinal);
        Assert.Contains("+working", working.Content, StringComparison.Ordinal);

        Assert.Contains("-base", staged.Content, StringComparison.Ordinal);
        Assert.Contains("+staged", staged.Content, StringComparison.Ordinal);

        Assert.Contains("-base", head.Content, StringComparison.Ordinal);
        Assert.Contains("+working", head.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffScopesItselfToTheRequestedPath()
    {
        using var sandbox = GitSandbox.Create();
        sandbox.Write("tracked.txt", "changed\n");
        sandbox.Write("other.txt", "changed\n");

        var result = await new GitDiffTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                "git_diff",
                ToolTestWorkspace.Args(new { scope = "working", path = "tracked.txt" })),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("tracked.txt", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("other.txt", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACleanRepositorySaysSoInsteadOfReturningAnEmptyResult()
    {
        using var sandbox = GitSandbox.Create();

        var status = await new GitStatusTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_status", "{}"),
            sandbox.Context,
            CancellationToken.None);
        var diff = await DiffAsync(sandbox, "head");

        Assert.False(status.IsError, status.Content);
        Assert.Contains("clean working tree", status.Content, StringComparison.Ordinal);
        Assert.False(diff.IsError, diff.Content);
        Assert.EndsWith("no changes", diff.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALargeDiffIsBoundedAndSaysSo()
    {
        using var sandbox = GitSandbox.Create();
        var before = string.Join('\n', Enumerable.Range(0, 4_000).Select(index => $"line {index}")) + "\n";
        var after = string.Join('\n', Enumerable.Range(0, 4_000).Select(index => $"changed {index}")) + "\n";
        sandbox.Write("tracked.txt", before);
        sandbox.Commit("large base");
        sandbox.Write("tracked.txt", after);

        var result = await DiffAsync(sandbox, "working");

        Assert.False(result.IsError, result.Content);
        Assert.True(result.IsTruncated);
        Assert.Contains(TextLimits.TruncatedMarker, result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length <= TextLimits.MaxCommandOutputChars);
    }

    [Fact]
    public async Task AnInvalidScopeIsARefusalRatherThanARevisionExpression()
    {
        using var sandbox = GitSandbox.Create();

        var result = await new GitDiffTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_diff", """{"scope":"HEAD~1"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("scope must be", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathOutsideTheWorkspaceIsRefusedBeforeGitStarts()
    {
        using var sandbox = GitSandbox.Create();

        var result = await new GitStatusTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_status", """{"path":"../outside"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("outside", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AWorkspaceThatIsNotARepositoryFailsWithGitsExplanation()
    {
        using var workspace = ToolTestWorkspace.Create();

        var result = await new GitStatusTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_status", "{}"),
            workspace.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("git status failed", result.Content, StringComparison.Ordinal);
        Assert.Contains("not a git repository", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingGitExecutableIsAFailureNotAnUnhandledException()
    {
        using var sandbox = GitSandbox.Create();

        var result = await new GitStatusTool(new ShellProcessRegistry(), "sharpcoreagent-no-such-git")
            .ExecuteAsync(
                ToolTestWorkspace.Call("git_status", "{}"),
                sandbox.Context,
                CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("could not start", result.Content, StringComparison.Ordinal);
        Assert.Contains("Is Git installed", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkspaceBelowTheRepositoryRootSeesOnlyItsOwnFiles()
    {
        using var sandbox = GitSandbox.Create();
        sandbox.Write("sub/inside.txt", "one\n");
        sandbox.Write("root-only.txt", "one\n");
        sandbox.Commit("two roots");
        sandbox.Write("sub/inside.txt", "changed\n");
        sandbox.Write("root-only.txt", "changed\n");
        var nested = Workspace.Create(Path.Combine(sandbox.Root, "sub"));
        var context = new ToolInvocationContext(nested);

        var result = await new GitStatusTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_status", "{}"),
            context,
            CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("sub/inside.txt", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("root-only.txt", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathThatLooksLikeAnOptionIsStillOnlyAPath()
    {
        using var sandbox = GitSandbox.Create();
        sandbox.Write("--output=evil.txt", "not an option\n");

        var result = await new GitStatusTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_status", """{"path":"--output=evil.txt"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("--output=evil.txt", result.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sandbox.Root, "evil.txt")));
    }

    private static Task<ToolResult> DiffAsync(GitSandbox sandbox, string scope) =>
        new GitDiffTool().ExecuteAsync(
            ToolTestWorkspace.Call("git_diff", ToolTestWorkspace.Args(new { scope })),
            sandbox.Context,
            CancellationToken.None).AsTask();
}
