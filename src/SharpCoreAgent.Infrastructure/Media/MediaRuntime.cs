using System.Globalization;
using System.Runtime.InteropServices;
using FFMediaToolkit;
using FFMediaToolkit.Decoding;
using SkiaSharp;

namespace SharpCoreAgent.Infrastructure.Media;

/// <summary>
/// Loads the LGPL FFmpeg runtime once and reports whether this platform has one.
/// </summary>
/// <remarks>
/// FFMediaToolkit is MIT; it binds FFmpeg through FFmpeg.AutoGen. The runtime package ships Windows and Linux
/// binaries, not macOS, so this type refuses the capability there instead of letting a video be mistaken for a still.
/// The alternative is replacing only this seam with another OSS decoder if FFmpeg's packaging changes.
/// </remarks>
internal static class MediaRuntime
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _error;

    /// <summary>Gets a value indicating whether this build can decode media on this platform.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>Tries to load the native libraries.</summary>
    /// <param name="error">Why loading failed, when it did.</param>
    /// <returns><see langword="true"/> when a media file can be opened.</returns>
    public static bool TryLoad(out string error)
    {
        lock (Gate)
        {
            if (_loaded)
            {
                error = string.Empty;
                return true;
            }

            if (_error is not null)
            {
                error = _error;
                return false;
            }

            if (!IsSupported)
            {
                _error = "audio/video decoding is unavailable on macOS in this build; the FFmpeg runtime ships Windows and Linux";
                error = _error;
                return false;
            }

            try
            {
                var native = Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    RuntimeInformation.RuntimeIdentifier,
                    "native");
                if (Directory.Exists(native))
                {
                    FFmpegLoader.FFmpegPath = native;
                }

                FFmpegLoader.LoadFFmpeg();
                _loaded = true;
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _error = $"the FFmpeg runtime could not be loaded: {ex.Message}";
                error = _error;
                return false;
            }
        }
    }

    /// <summary>Formats a duration without a locale-dependent colon shape.</summary>
    public static string Duration(TimeSpan duration) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
}
