using SharpCoreAgent.Domain;
using Xunit;

namespace SharpCoreAgent.Tests.Domain;

/// <summary>
/// <see cref="Workspace"/> is the one place that decides whether a path may be touched, so the tests
/// walk the containment matrix a tool would otherwise have to repeat: absolute paths outside, relative
/// escapes, a sibling directory that shares a prefix, the root itself, and the platform's case rules.
/// </summary>
public sealed class WorkspaceTests
{
    [Fact]
    public void CreateNormalisesTheRootAndDropsATrailingSeparator()
    {
        using var sandbox = Sandbox.Create();

        var workspace = Workspace.Create(sandbox.Root + Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(sandbox.Root), workspace.Root);
        Assert.NotEqual(Path.DirectorySeparatorChar, workspace.Root[^1]);
        Assert.NotEqual(Path.AltDirectorySeparatorChar, workspace.Root[^1]);
    }

    [Fact]
    public void CreateRejectsAMissingDirectory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sharpcoreagent-workspace", Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => Workspace.Create(missing));
    }

    [Fact]
    public void CreateRejectsABlankRoot()
    {
        Assert.ThrowsAny<ArgumentException>(() => Workspace.Create(" "));
        Assert.ThrowsAny<ArgumentException>(() => Workspace.Create(null!));
    }

    [Fact]
    public void ARelativePathInsideTheRootResolves()
    {
        using var sandbox = Sandbox.Create();
        var workspace = sandbox.Workspace;

        Assert.True(workspace.TryResolve("src/deep/App.cs", out var full, out var error));
        Assert.Null(error);
        Assert.Equal(Path.Combine(workspace.Root, "src", "deep", "App.cs"), full);
    }

    [Fact]
    public void ARelativeEscapeIsRejectedAndTheReasonNamesTheRoot()
    {
        using var sandbox = Sandbox.Create();
        var workspace = sandbox.Workspace;

        Assert.False(workspace.TryResolve("../../../etc/passwd", out var full, out var error));
        Assert.Equal(string.Empty, full);
        Assert.Contains("resolves outside the workspace root", error!, StringComparison.Ordinal);
        Assert.Contains(workspace.Root, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsolutePathOutsideTheRootIsRejected()
    {
        using var sandbox = Sandbox.Create();
        var outside = Path.Combine(Path.GetTempPath(), "sharpcoreagent-outside-" + Guid.NewGuid().ToString("N"), "file.txt");

        Assert.False(sandbox.Workspace.TryResolve(outside, out _, out var error));
        Assert.Contains("outside the workspace root", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsolutePathInsideTheRootIsAccepted()
    {
        using var sandbox = Sandbox.Create();
        var inside = Path.Combine(sandbox.Root, "notes.txt");

        Assert.True(sandbox.Workspace.TryResolve(inside, out var full, out _));
        Assert.Equal(inside, full);
    }

    [Fact]
    public void ANullOrNulBearingPathIsRejected()
    {
        using var sandbox = Sandbox.Create();

        Assert.False(sandbox.Workspace.TryResolve(null!, out _, out var missing));
        Assert.Equal("path is required", missing);

        Assert.False(sandbox.Workspace.TryResolve("a\0b", out _, out var nul));
        Assert.Equal("path contains a NUL character", nul);
    }

    [Fact]
    public void ASiblingDirectoryThatSharesAPrefixIsNotInside()
    {
        using var sandbox = Sandbox.Create();
        var workspace = sandbox.Workspace;
        var sibling = workspace.Root + "-copy";

        Assert.True(workspace.IsInside(workspace.Root));
        Assert.True(workspace.IsInside(Path.Combine(workspace.Root, "src", "App.cs")));
        Assert.False(workspace.IsInside(sibling));
    }

    [Fact]
    public void ToRelativeUsesForwardSlashesAndTheRootBecomesADot()
    {
        using var sandbox = Sandbox.Create();
        var workspace = sandbox.Workspace;

        Assert.Equal(".", workspace.ToRelative(workspace.Root));
        Assert.Equal("src/deep/App.cs", workspace.ToRelative(Path.Combine(workspace.Root, "src", "deep", "App.cs")));
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "elsewhere"),
            workspace.ToRelative(Path.Combine(Path.GetTempPath(), "elsewhere")));
    }

    [Fact]
    public void CasingFollowsThePlatform()
    {
        using var sandbox = Sandbox.Create();
        var workspace = sandbox.Workspace;
        var upper = Path.Combine(workspace.Root, "sub").ToUpperInvariant();

        Assert.Equal(OperatingSystem.IsWindows(), workspace.IsInside(upper));
    }

    private sealed class Sandbox : IDisposable
    {
        private Sandbox(string root)
        {
            Root = root;
            Workspace = Workspace.Create(root);
        }

        public string Root { get; }

        public Workspace Workspace { get; }

        public static Sandbox Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "sharpcoreagent-workspace", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Sandbox(root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }
}
