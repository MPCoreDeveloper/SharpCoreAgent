using SharpCoreAgent.Infrastructure.Images;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The command-line image loader must use the same extension, size and workspace rules as the tools.
/// </summary>
public sealed class ImageFileTests
{
    [Fact]
    public async Task APngInsideTheWorkspaceBecomesAnAttachableImage()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var path = Path.Combine(sandbox.Root, "shot.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        Assert.True(ImageFile.TryLoad(sandbox.Workspace, "shot.png", out var image, out var error), error);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), image.Base64);
    }

    [Fact]
    public void AnUnsupportedExtensionIsExplained()
    {
        using var sandbox = ToolTestWorkspace.Create();
        File.WriteAllBytes(Path.Combine(sandbox.Root, "note.txt"), [1]);

        Assert.False(ImageFile.TryLoad(sandbox.Workspace, "note.txt", out _, out var error));
        Assert.Contains("picture types", error, StringComparison.Ordinal);
    }

    [Fact]
    public void APathOutsideTheWorkspaceIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();

        Assert.False(ImageFile.TryLoad(sandbox.Workspace, "../outside.png", out _, out var error));
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnImageOverThePerFileCeilingIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        File.WriteAllBytes(Path.Combine(sandbox.Root, "large.png"), new byte[4 * 1024 * 1024 + 1]);

        Assert.False(ImageFile.TryLoad(sandbox.Workspace, "large.png", out _, out var error));
        Assert.Contains("4 MiB", error, StringComparison.Ordinal);
    }
}
