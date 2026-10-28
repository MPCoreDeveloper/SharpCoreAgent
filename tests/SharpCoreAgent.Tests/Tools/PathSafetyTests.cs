using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The physical check, which is the half of containment a lexical comparison cannot answer: a link inside a
/// granted tree can still point anywhere.
/// </summary>
/// <remarks>
/// Attaching a directory must not become a way to reach <c>/etc</c> or <c>C:\Windows</c>, and a link that lands in
/// the primary root is still inside what the developer granted. Both directions are asserted here, because only
/// the pair shows that the check is "inside any root" rather than "inside the last root that matched".
/// </remarks>
public sealed class PathSafetyTests
{
    [Fact]
    public void ALinkInsideAnAttachmentThatEscapesEveryRootIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var attachment = sandbox.AddAttachment("shared");
        var outside = Directory.CreateDirectory(
            Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "outside-" + Guid.NewGuid().ToString("N")));

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(attachment, "elsewhere"), outside.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symbolic links are not available here: {ex.Message}");
            return;
        }

        Assert.False(PathSafety.TryResolveRead(
            sandbox.Workspace,
            Path.Combine(attachment, "elsewhere", "secrets.txt"),
            out var full,
            out var owner,
            out var error));

        Assert.Equal(string.Empty, full);
        Assert.Null(owner);
        Assert.Contains("resolves through a link", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkInsideAnAttachmentThatLandsInThePrimaryRootIsAccepted()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var attachment = sandbox.AddAttachment("shared");
        var target = sandbox.Write("src/Target.cs", "class T { }");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(attachment, "here"), Path.Combine(sandbox.Root, "src"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symbolic links are not available here: {ex.Message}");
            return;
        }

        Assert.True(PathSafety.TryResolveRead(
            sandbox.Workspace,
            Path.Combine(attachment, "here", "Target.cs"),
            out var full,
            out var owner,
            out var error));

        Assert.Null(error);
        Assert.Equal(Path.Combine(attachment, "here", "Target.cs"), full);
        Assert.Same(sandbox.Workspace.Attachments[0], owner);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ALinkInsideAnAttachmentThatPointsAtItselfTerminates()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var attachment = sandbox.AddAttachment("shared");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(attachment, "self"), attachment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symbolic links are not available here: {ex.Message}");
            return;
        }

        // Three hops of a self-referencing link: the walk has to stop, and the answer has to be a refusal or an
        // acceptance rather than a hang or a stack overflow.
        var resolved = PathSafety.TryResolveRead(
            sandbox.Workspace,
            Path.Combine(attachment, "self", "self", "self", "notes.txt"),
            out var full,
            out _,
            out var error);

        Assert.True(resolved || full.Length == 0);
        Assert.True(resolved ? error is null : error!.Contains("resolves through a link", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWritePathStillRefusesALinkOutOfThePrimaryRoot()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var attachment = sandbox.AddAttachment("shared");
        sandbox.Write("src/keep.cs", "class K { }");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(sandbox.Root, "shared"), attachment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symbolic links are not available here: {ex.Message}");
            return;
        }

        // An attachment is readable, never writable, even by a link the developer placed inside the primary root.
        Assert.False(PathSafety.TryResolve(
            sandbox.Workspace,
            "shared/notes.txt",
            out _,
            out var error));
        Assert.Contains("resolves through a link", error!, StringComparison.Ordinal);
    }
}
