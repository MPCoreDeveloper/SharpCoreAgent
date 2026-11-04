using SharpCoreAgent.Domain;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>The ignore syntax the three scanners depend on.</summary>
public sealed class GitIgnoreMatcherTests
{
    [Fact]
    public void RootRulesCoverNamesDirectoriesAndAnchoredPaths()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write(".gitignore", "*.log\n!keep.log\nbuild/\n/root-only.txt\n");
        var matcher = GitIgnoreMatcher.For(sandbox.Root);

        Assert.True(matcher.IsIgnored(sandbox.FullPath("a.log"), isDirectory: false));
        Assert.True(matcher.IsIgnored(sandbox.FullPath("nested/b.log"), isDirectory: false));
        Assert.False(matcher.IsIgnored(sandbox.FullPath("keep.log"), isDirectory: false));
        Assert.True(matcher.IsIgnored(sandbox.FullPath("build"), isDirectory: true));
        Assert.True(matcher.IsIgnored(sandbox.FullPath("build/out.txt"), isDirectory: false));
        Assert.False(matcher.IsIgnored(sandbox.FullPath("build"), isDirectory: false));
        Assert.True(matcher.IsIgnored(sandbox.FullPath("root-only.txt"), isDirectory: false));
        Assert.False(matcher.IsIgnored(sandbox.FullPath("nested/root-only.txt"), isDirectory: false));
    }

    [Fact]
    public void NestedRulesApplyFromTheirDirectoryAndNegationWins()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write(".gitignore", "*.tmp\n");
        sandbox.Write("src/.gitignore", "!important.tmp\nlocal.txt\n");
        var matcher = GitIgnoreMatcher.For(sandbox.Root);
        matcher.LoadAncestors(sandbox.FullPath("src"));

        Assert.False(matcher.IsIgnored(sandbox.FullPath("src/important.tmp"), isDirectory: false));
        Assert.True(matcher.IsIgnored(sandbox.FullPath("src/local.txt"), isDirectory: false));
        Assert.False(matcher.IsIgnored(sandbox.FullPath("other/local.txt"), isDirectory: false));
        Assert.True(matcher.IsIgnored(sandbox.FullPath("other/other.tmp"), isDirectory: false));
    }

    [Fact]
    public void ADeepSearchLoadsNestedRulesInRootToLeafOrder()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write(".gitignore", "*.tmp\n");
        sandbox.Write("src/.gitignore", "!mid.tmp\n");
        sandbox.Write("src/deep/.gitignore", "!deep.tmp\n");
        var matcher = GitIgnoreMatcher.For(sandbox.Root);
        matcher.LoadAncestors(sandbox.FullPath("src/deep"));

        Assert.True(matcher.IsIgnored(sandbox.FullPath("src/deep/other.tmp"), isDirectory: false));
        Assert.False(matcher.IsIgnored(sandbox.FullPath("src/deep/mid.tmp"), isDirectory: false));
        Assert.False(matcher.IsIgnored(sandbox.FullPath("src/deep/deep.tmp"), isDirectory: false));
    }

    [Fact]
    public void DoubleStarCrossesDirectories()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write(".gitignore", "**/generated/**\n");
        var matcher = GitIgnoreMatcher.For(sandbox.Root);

        Assert.True(matcher.IsIgnored(sandbox.FullPath("generated/a.txt"), isDirectory: false));
        Assert.True(matcher.IsIgnored(sandbox.FullPath("src/generated/deep/a.txt"), isDirectory: false));
        Assert.False(matcher.IsIgnored(sandbox.FullPath("src/kept/a.txt"), isDirectory: false));
    }

    [Fact]
    public void AnUnreadableIgnoreFileLeavesTheTreeVisible()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var matcher = GitIgnoreMatcher.For(sandbox.Root);

        Assert.False(matcher.IsIgnored(sandbox.FullPath("a.txt"), isDirectory: false));
    }
}
