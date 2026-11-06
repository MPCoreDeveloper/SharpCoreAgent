using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The media tool: audio metadata is text, a non-media file is refused, and the FFmpeg runtime is reached through the
/// package rather than a child process.
/// </summary>
public sealed class ViewMediaToolTests
{
    [Fact]
    public async Task AudioIsDescribedWithoutAnImage()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("sounds/tone.wav", Wav());

        var result = await new ViewMediaTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_media", """{"path":"sounds/tone.wav"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("audio", result.Content, StringComparison.Ordinal);
        Assert.Contains("8000 Hz", result.Content, StringComparison.Ordinal);
        Assert.Empty(result.Images);
    }

    [Fact]
    public async Task ANonMediaFileIsRefusedByName()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("notes.txt", "not media");

        var result = await new ViewMediaTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_media", """{"path":"notes.txt"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("view_image", result.Content, StringComparison.Ordinal);
    }

    private static byte[] Wav()
    {
        const int sampleRate = 8_000;
        const int seconds = 1;
        const int dataBytes = sampleRate * seconds * 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        writer.Flush();
        return stream.ToArray();
    }
}
