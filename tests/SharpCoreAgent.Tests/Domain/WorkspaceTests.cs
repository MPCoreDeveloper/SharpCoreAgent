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

    [Fact]
    public void AnAttachmentIsReadableThroughTheReadPathAndNotTheWritePath()
    {
        using var sandbox = Sandbox.Create();
        var shared = sandbox.CreateDirectory("shared");
        var file = Path.Combine(shared, "src", "Order.cs");

        var workspace = sandbox.Workspace.WithAttachments([shared]);

        Assert.True(workspace.HasAttachments);
        Assert.Single(workspace.Attachments);
        Assert.Equal(Path.GetFullPath(shared), workspace.Attachments[0].Root);

        // The read path accepts the absolute path and names the attachment that answered it.
        Assert.True(workspace.TryResolveRead(file, out var full, out var owner, out var error));
        Assert.Null(error);
        Assert.Equal(file, full);
        Assert.Same(workspace.Attachments[0], owner);

        // The write path did not move: the same path is still outside the workspace root.
        Assert.False(workspace.TryResolve(file, out _, out var writeError));
        Assert.Contains("resolves outside the workspace root", writeError!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativePathAlwaysMeansThePrimaryRootEvenWhenAnAttachmentHoldsTheSameName()
    {
        using var sandbox = Sandbox.Create();
        var shared = sandbox.CreateDirectory("shared");
        var primary = Path.Combine(sandbox.Root, "src", "Order.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
        File.WriteAllText(primary, "primary");
        Directory.CreateDirectory(Path.Combine(shared, "src"));
        File.WriteAllText(Path.Combine(shared, "src", "Order.cs"), "attachment");

        var workspace = sandbox.Workspace.WithAttachments([shared]);

        Assert.True(workspace.TryResolveRead("src/Order.cs", out var full, out var owner, out _));
        Assert.Equal(primary, full);
        Assert.Null(owner);
    }

    [Fact]
    public void AnAbsolutePathInsideThePrimaryRootResolvesWithoutAnOwner()
    {
        using var sandbox = Sandbox.Create();
        var shared = sandbox.CreateDirectory("shared");
        var workspace = sandbox.Workspace.WithAttachments([shared]);

        Assert.True(workspace.TryResolveRead(Path.Combine(sandbox.Root, "notes.txt"), out _, out var owner, out _));
        Assert.Null(owner);
    }

    [Fact]
    public void AnAbsolutePathInsideNoRootIsRefusedAndPointsAtListRoots()
    {
        using var sandbox = Sandbox.Create();
        var shared = sandbox.CreateDirectory("shared");
        var proto = sandbox.CreateDirectory("proto");
        var workspace = sandbox.Workspace.WithAttachments([shared, proto]);
        var outside = Path.Combine(Path.GetTempPath(), "sharpcoreagent-outside-" + Guid.NewGuid().ToString("N"), "file.txt");

        Assert.False(workspace.TryResolveRead(outside, out var full, out var owner, out var error));
        Assert.Equal(string.Empty, full);
        Assert.Null(owner);
        Assert.Contains("2 read-only attachments", error!, StringComparison.Ordinal);
        Assert.Contains("list_roots", error!, StringComparison.Ordinal);
        Assert.Contains(workspace.Root, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkspaceWithoutAttachmentsKeepsTheOriginalRefusalWording()
    {
        using var sandbox = Sandbox.Create();
        var outside = Path.Combine(Path.GetTempPath(), "sharpcoreagent-outside-" + Guid.NewGuid().ToString("N"), "file.txt");

        Assert.False(sandbox.Workspace.TryResolveRead(outside, out _, out _, out var error));
        Assert.Equal($"path '{outside}' resolves outside the workspace root '{sandbox.Workspace.Root}'", error);
    }

    [Fact]
    public void WithAttachmentsDropsDuplicatesAndNestedRoots()
    {
        using var sandbox = Sandbox.Create();
        var shared = sandbox.CreateDirectory("shared");
        var nested = Directory.CreateDirectory(Path.Combine(shared, "proto")).FullName;

        var workspace = sandbox.Workspace.WithAttachments([shared, shared, nested, sandbox.Root]);

        Assert.Single(workspace.Attachments);
        Assert.Equal(Path.GetFullPath(shared), workspace.Attachments[0].Root);
    }

    [Fact]
    public void WithAttachmentsDropsABlankEntryAndReturnsANewValue()
    {
        using var sandbox = Sandbox.Create();
        var shared = sandbox.CreateDirectory("shared");

        var workspace = sandbox.Workspace.WithAttachments([string.Empty, shared]);

        Assert.Single(workspace.Attachments);
        Assert.False(sandbox.Workspace.HasAttachments);
        Assert.NotSame(sandbox.Workspace, workspace);
        Assert.Equal(sandbox.Workspace.Root, workspace.Root);
    }

    [Fact]
    public void AMissingOrFileAttachmentRootIsRefusedWithItsOwnWording()
    {
        using var sandbox = Sandbox.Create();
        var missing = Path.Combine(Path.GetTempPath(), "sharpcoreagent-missing-" + Guid.NewGuid().ToString("N"));

        var absent = Assert.Throws<DirectoryNotFoundException>(() => sandbox.Workspace.WithAttachments([missing]));
        Assert.Contains("attachment root", absent.Message, StringComparison.Ordinal);
        Assert.Contains(missing, absent.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", absent.Message, StringComparison.Ordinal);

        var file = Path.Combine(sandbox.Root, "notes.txt");
        File.WriteAllText(file, "text");
        var wrongKind = Assert.Throws<DirectoryNotFoundException>(() => sandbox.Workspace.WithAttachments([file]));
        Assert.Contains("is a file, not a directory", wrongKind.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsInsideAnyAnswersForEveryRootAndDescribeOutsideNamesTheAttachment()
    {
        using var sandbox = Sandbox.Create();
        var shared = sandbox.CreateDirectory("shared");
        var workspace = sandbox.Workspace.WithAttachments([shared]);
        var attachmentFile = Path.Combine(shared, "src", "Order.cs");

        Assert.True(workspace.IsInsideAny(workspace.Root));
        Assert.True(workspace.IsInsideAny(attachmentFile));
        Assert.False(workspace.IsInsideAny(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        Assert.Equal(
            $"'{workspace.Attachments[0].Root}' is a read-only attachment; writes stay in the primary root.",
            workspace.DescribeOutside(attachmentFile));

        // An ordinary escape gets no hint: the resolver's own message already describes it.
        Assert.Null(workspace.DescribeOutside(Path.Combine(Path.GetTempPath(), "elsewhere", "file.txt")));
        Assert.Null(workspace.DescribeOutside("src/App.cs"));
    }

    private sealed class Sandbox : IDisposable
    {
        private Sandbox(string parent, string root)
        {
            Parent = parent;
            Root = root;
            Workspace = Workspace.Create(root);
        }

        public string Parent { get; }

        public string Root { get; }

        public Workspace Workspace { get; }

        public static Sandbox Create()
        {
            var parent = Path.Combine(Path.GetTempPath(), "sharpcoreagent-workspace", Guid.NewGuid().ToString("N"));
            var root = Path.Combine(parent, "app");
            Directory.CreateDirectory(root);
            return new Sandbox(parent, root);
        }

        /// <summary>
        /// Creates a directory beside the primary root, so it can be attached instead of dropped as nested.
        /// </summary>
        /// <param name="name">The directory name.</param>
        /// <returns>The absolute path.</returns>
        public string CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(Parent, name)).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Parent, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }
}
