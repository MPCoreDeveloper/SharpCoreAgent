using SharpCoreAgent.Indexing.Extraction;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The scanner decides what the index will ever know about, so these tests are about the boundary: what must
/// never be walked, what must be recorded as skipped instead of silently dropped, and in what order the walk
/// comes out.
/// </summary>
public sealed class WorkspaceFileScannerTests
{
    [Fact]
    public void IgnoresTheDirectoriesNobodyWantsIndexed()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Keep.cs", "namespace N; public class C { }");
        sandbox.Write(".git/objects/Blob.cs", "not source");
        sandbox.Write("node_modules/pkg/Index.js", "module.exports = 1;");
        sandbox.Write("bin/Debug/Output.cs", "nope");
        sandbox.Write("obj/project.assets.json", "{}");
        sandbox.Write(".vs/state.cs", "nope");
        sandbox.Write(".sharpcoreagent/index/Own.cs", "nope");

        var scanned = Scan(sandbox);

        Assert.Equal(["src/Keep.cs"], scanned.Select(file => file.Path));
    }

    [Fact]
    public void RecordsSkippedFilesWithAReason()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Keep.cs", "namespace N; public class C { }");
        sandbox.WriteBytes("assets/logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        sandbox.Write("docs/Huge.md", new string('x', (int)WorkspaceFileScanner.MaxFileBytes + 1));

        var scanned = Scan(sandbox);

        var binary = Assert.Single(scanned, file => file.Path == "assets/logo.png");
        Assert.False(binary.IsIndexable);
        Assert.Equal("binary", binary.SkipReason);

        var huge = Assert.Single(scanned, file => file.Path == "docs/Huge.md");
        Assert.False(huge.IsIndexable);
        Assert.Equal("larger than 1024 KiB", huge.SkipReason);

        var kept = Assert.Single(scanned, file => file.Path == "src/Keep.cs");
        Assert.True(kept.IsIndexable);
        Assert.True(kept.SizeBytes > 0);
        Assert.NotEqual(default, kept.ModifiedUtc);
    }

    [Fact]
    public void OrdersOrdinallyAndStablyAcrossRuns()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("b.cs", "class B { }");
        sandbox.Write("A.cs", "class A { }");
        sandbox.Write("a/Z.cs", "class Z { }");
        sandbox.Write("a/B.cs", "class AB { }");

        var first = Scan(sandbox).Select(file => file.Path).ToArray();
        var second = Scan(sandbox).Select(file => file.Path).ToArray();

        // Ordinal, not culture-aware: uppercase sorts before lowercase, so the order is a property of the bytes
        // and not of the machine's locale.
        Assert.Equal(["A.cs", "a/B.cs", "a/Z.cs", "b.cs"], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void NamesTheLanguageTheExtensionImplies()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("Service.cs", "class S { }");
        sandbox.Write("README.md", "# Title");
        sandbox.Write("run.ps1", "Write-Host 1");
        sandbox.Write("Makefile.am", "all:");

        var byPath = Scan(sandbox).ToDictionary(file => file.Path, StringComparer.Ordinal);

        Assert.Equal("csharp", byPath["Service.cs"].Language);
        Assert.Equal("markdown", byPath["README.md"].Language);
        Assert.Equal("powershell", byPath["run.ps1"].Language);

        // An unknown extension is still text until its bytes say otherwise, so it is indexed rather than
        // guessed at by name.
        Assert.Equal("text", byPath["Makefile.am"].Language);
        Assert.True(byPath["Makefile.am"].IsIndexable);
    }

    [Fact]
    public void UsesTheSameIgnoreListAsTheWorkspaceTools()
    {
        // SharpCoreAgent.Indexing cannot reference SharpCoreAgent.Infrastructure, so the list is duplicated on purpose.
        // This test is the pin that keeps the copy honest.
        Assert.Equal(IgnoredPaths.DirectoryNames, WorkspaceFileScanner.IgnoredDirectoryNames);
        Assert.Equal(TextLimits.MaxSearchableFileBytes, WorkspaceFileScanner.MaxFileBytes);
    }

    [Fact]
    public void DoesNotWalkTheSameDirectoryTwiceThroughALink()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var target = sandbox.Write("src/Target.cs", "class T { }");
        Assert.NotNull(target);

        try
        {
            Directory.CreateSymbolicLink(sandbox.FullPath("loop"), sandbox.FullPath("src"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a link needs a privilege the test runner may not have; the rule is still covered by the
            // visited set, which counts a path once however it is reached.
            Assert.Skip($"symbolic links are not available here: {ex.Message}");
            return;
        }

        var scanned = Scan(sandbox);

        Assert.Equal(["src/Target.cs"], scanned.Select(file => file.Path));
    }

    private static IReadOnlyList<ScannedFile> Scan(ToolTestWorkspace sandbox) =>
        WorkspaceFileScanner.For(sandbox.Workspace).Scan(TestContext.Current.CancellationToken);
}
