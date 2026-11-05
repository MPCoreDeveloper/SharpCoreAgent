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
    public void StdinWithAPngSignatureBecomesAnImage()
    {
        var bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 0, 0, 0, 0, 0,
        };

        Assert.True(ImageFile.TryLoadStdin(new MemoryStream(bytes), out var image, out var error), error);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Convert.ToBase64String(bytes), image.Base64);
    }

    [Fact]
    public void StdinWithNoKnownSignatureIsRefused()
    {
        Assert.False(ImageFile.TryLoadStdin(new MemoryStream("not a picture"u8.ToArray()), out _, out var error));
        Assert.Contains("not a PNG, JPEG, GIF or WebP", error, StringComparison.Ordinal);
    }

    [Fact]
    public void StdinOverThePerFileCeilingIsRefused()
    {
        var bytes = new byte[4 * 1024 * 1024 + 1];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;

        Assert.False(ImageFile.TryLoadStdin(new MemoryStream(bytes), out _, out var error));
        Assert.Contains("4 MiB", error, StringComparison.Ordinal);
    }

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
    public void AnSvgInsideTheWorkspaceIsRenderedToPng()
    {
        using var sandbox = ToolTestWorkspace.Create();
        File.WriteAllText(
            Path.Combine(sandbox.Root, "diagram.svg"),
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="40" viewBox="0 0 120 40">
              <rect width="120" height="40" fill="#0f0"/>
            </svg>
            """);

        Assert.True(ImageFile.TryLoad(sandbox.Workspace, "diagram.svg", out var image, out var error), error);
        Assert.Equal("image/png", image.MediaType);

        var bytes = Convert.FromBase64String(image.Base64);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
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
