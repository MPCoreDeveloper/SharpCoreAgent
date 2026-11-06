using System.Drawing;
using System.Globalization;
using FFMediaToolkit.Decoding;
using FFMediaToolkit.Graphics;
using SkiaSharp;
using SharpCoreAgent.Infrastructure.Images;

namespace SharpCoreAgent.Infrastructure.Media;

/// <summary>
/// Reads audio/video metadata and one video frame through FFMediaToolkit.
/// </summary>
/// <remarks>
/// A video frame is an image only after it has been decoded and encoded as PNG; audio has no frame and is described by
/// its stream metadata. The caller decides whether the session's model may see the frame, exactly as it does for every
/// other image door.
/// </remarks>
internal static class MediaInspector
{
    /// <summary>Describes a media file and reports whether it has a video stream.</summary>
    /// <param name="path">The absolute file path.</param>
    /// <param name="description">The text to hand the model.</param>
    /// <param name="hasVideo">Whether a frame can be extracted.</param>
    /// <param name="duration">The container duration.</param>
    /// <param name="error">Why the file could not be read.</param>
    /// <returns><see langword="true"/> when metadata was read.</returns>
    public static bool TryDescribe(
        string path,
        out string description,
        out bool hasVideo,
        out TimeSpan duration,
        out string error)
    {
        description = string.Empty;
        hasVideo = false;
        duration = TimeSpan.Zero;
        if (!MediaRuntime.TryLoad(out error))
        {
            return false;
        }

        try
        {
            using var file = MediaFile.Open(path, new MediaOptions
            {
                StreamsToLoad = MediaMode.AudioVideo,
                VideoPixelFormat = ImagePixelFormat.Rgba32,
            });

            hasVideo = file.HasVideo;
            duration = file.Info.Duration;
            var text = new System.Text.StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"container {file.Info.ContainerFormat}");
            text.Append(CultureInfo.InvariantCulture, $" · {MediaRuntime.Duration(file.Info.Duration)}");
            text.Append(CultureInfo.InvariantCulture, $" · {file.Info.Bitrate / 1000.0:0} kb/s");

            if (file.HasVideo)
            {
                var video = file.Video.Info;
                text.Append(CultureInfo.InvariantCulture, $"\nvideo {video.CodecName} · {video.FrameSize.Width} × {video.FrameSize.Height} · {video.AvgFrameRate:0.##} fps");
            }

            if (file.HasAudio)
            {
                var audio = file.Audio.Info;
                text.Append(CultureInfo.InvariantCulture, $"\naudio {audio.CodecName} · {audio.SampleRate} Hz · {audio.NumChannels} channel(s)");
            }

            description = text.ToString();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"the media metadata could not be read: {ex.Message}";
            return false;
        }
    }
 
    /// <summary>Decodes one video frame and encodes it as PNG.</summary>
    /// <param name="path">The absolute file path.</param>
    /// <param name="atSeconds">The timestamp in seconds, zero-based.</param>
    /// <param name="maxDimension">The largest result dimension, or zero to keep the frame's size.</param>
    /// <param name="maxBytes">The PNG ceiling.</param>
    /// <param name="png">The PNG, on success.</param>
    /// <param name="width">The PNG width.</param>
    /// <param name="height">The PNG height.</param>
    /// <param name="error">Why the frame could not be decoded.</param>
    /// <returns><see langword="true"/> when a frame was produced.</returns>
    public static bool TryFrame(
        string path,
        int atSeconds,
        int maxDimension,
        long maxBytes,
        out byte[] png,
        out int width,
        out int height,
        out string error)
    {
        png = [];
        width = 0;
        height = 0;
        if (!MediaRuntime.TryLoad(out error))
        {
            return false;
        }

        try
        {
            using var file = MediaFile.Open(path, new MediaOptions
            {
                StreamsToLoad = MediaMode.Video,
                VideoPixelFormat = ImagePixelFormat.Rgba32,
            });
            if (!file.HasVideo)
            {
                error = "the file has no video stream";
                return false;
            }

            var at = TimeSpan.FromSeconds(atSeconds);
            if (at > file.Info.Duration)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the video is {file.Info.Duration.TotalSeconds:0.#} seconds long; {atSeconds} is outside it");
                return false;
            }

            using var frame = file.Video.GetFrame(at);
            var size = frame.ImageSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                error = "the video frame has no usable size";
                return false;
            }

            using var bitmap = new SKBitmap(
                new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var pixels = bitmap.GetPixelSpan();
            var rowBytes = size.Width * 4;
            for (var y = 0; y < size.Height; y++)
            {
                frame.Data.Slice(y * frame.Stride, rowBytes).CopyTo(pixels.Slice(y * rowBytes, rowBytes));
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null)
            {
                error = "the video frame could not be encoded as PNG";
                return false;
            }

            var encoded = data.ToArray();
            if (maxDimension > 0 && !ImageResize.TryResize(
                    encoded,
                    maxDimension,
                    out encoded,
                    out width,
                    out height,
                    out _,
                    out var resizeError))
            {
                error = resizeError;
                return false;
            }

            if (width == 0 || height == 0)
            {
                width = size.Width;
                height = size.Height;
            }

            if (encoded.LongLength > maxBytes)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the decoded frame is {encoded.LongLength} bytes; the picture ceiling is {maxBytes} bytes; ask for a smaller max_dimension");
                return false;
            }

            png = encoded;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"the video frame could not be decoded: {ex.Message}";
            return false;
        }
    }
}

