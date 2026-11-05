using System.Globalization;
using System.Text;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The fixed sizes that keep one tool call from filling the model's context.
/// </summary>
/// <remarks>
/// These are the tool's own natural limits; the caller still applies
/// <c>AgentOptions.MaxToolResultChars</c> on top. Both exist because they answer different questions:
/// this one keeps a single call from reading a gigabyte, and the caller's budget keeps a whole turn's
/// worth of tool output inside the window.
/// <para>
/// Every shortened result ends with <see cref="TruncatedMarker"/> so the model can tell the difference
/// between "the file ends here" and "you are not being shown the rest".
/// </para>
/// </remarks>
public static class TextLimits
{
    /// <summary>Gets the most lines one <c>read_file</c> call returns.</summary>
    public const int MaxFileLines = 2_000;

    /// <summary>Gets the largest number of characters one <c>read_file</c> call returns.</summary>
    public const int MaxFileReadChars = 256 * 1024;

    /// <summary>Gets the largest number of matches one <c>search_text</c> call returns, whatever was asked for.</summary>
    public const int MaxSearchMatches = 200;

    /// <summary>Gets the largest number of entries one <c>list_directory</c> call returns.</summary>
    public const int MaxDirectoryEntries = 500;

    /// <summary>Gets the largest number of characters of command output one <c>run_shell</c> call keeps.</summary>
    public const int MaxCommandOutputChars = 64 * 1024;

    /// <summary>Gets the size above which a file is not searched, in bytes.</summary>
    public const long MaxSearchableFileBytes = 1024 * 1024;

    /// <summary>Gets the largest image one <c>view_image</c> call shows, in bytes.</summary>
    /// <remarks>
    /// Four mebibytes. Base64 spends a third on top of that, so one image costs about 5.6 MB of request body — well
    /// under every provider's ceiling — and a full-page screenshot, a diagram or a photograph at a readable scale
    /// still fits. The limit is on the file, not on what the model charges for it: a provider bills an image by its
    /// pixels, so a large picture costs tokens whether it is compressed or not.
    /// </remarks>
    public const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>Gets the largest number of images one tool result attaches, whatever the tool returned.</summary>
    /// <remarks>
    /// Four. A browser server can answer one call with a screenshot, a full-page capture and a console image or two,
    /// and four is a turn a developer can still read and a request a provider still accepts. A fifth picture is named
    /// in the result's text with the reason rather than dropped: an image that silently failed to arrive invites the
    /// model to invent it, which is the failure round 9 was built to prevent.
    /// </remarks>
    public const int MaxImagesPerResult = 4;

    /// <summary>Gets how many leading bytes are inspected when deciding whether a file is binary.</summary>
    public const int BinarySniffBytes = 8 * 1024;

    /// <summary>Gets the largest number of characters one OCR answer returns.</summary>
    /// <remarks>
    /// Sixteen thousand characters is a dense page of OCR text; the model usually needs the few lines it asked about,
    /// and the turn's result ceiling still applies on top.
    /// </remarks>
    public const int MaxOcrChars = 16 * 1024;

    /// <summary>Gets the largest number of characters one code-intelligence answer returns.</summary>
    /// <remarks>
    /// One file's diagnostics, or one symbol's uses, are bounded by the code itself rather than by this number; it
    /// exists so a generated file with ten thousand diagnostics cannot fill a turn's context in one call.
    /// </remarks>
    public const int MaxCodeIntelligenceChars = 24 * 1024;

    /// <summary>Gets the marker appended to a result that was shortened.</summary>
    public const string TruncatedMarker = "[truncated]";

    /// <summary>Gets UTF-8 without a byte-order mark, which is what every editor in the workspace expects.</summary>
    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Shortens text to a character budget.
    /// </summary>
    /// <param name="text">The text to shorten.</param>
    /// <param name="maxChars">The largest allowed length; a non-positive value means "no limit".</param>
    /// <param name="truncated">Whether anything was removed.</param>
    /// <returns>The original text, or its head followed by <see cref="TruncatedMarker"/>.</returns>
    public static string Bound(string text, int maxChars, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxChars <= 0 || text.Length <= maxChars)
        {
            truncated = false;
            return text;
        }

        truncated = true;
        var keep = Math.Max(0, maxChars - TruncatedMarker.Length - 1);
        return string.Create(CultureInfo.InvariantCulture, $"{text[..keep]}\n{TruncatedMarker}");
    }

    /// <summary>
    /// Determines whether the first bytes of a file look like binary content.
    /// </summary>
    /// <param name="head">The file's leading bytes, at most <see cref="BinarySniffBytes"/>.</param>
    /// <returns><see langword="true"/> when a NUL byte appears in the sample.</returns>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        // A NUL byte in the first few kilobytes is the cheapest signal that a text reader would
        // produce a screenful of mojibake, and it is the rule the contract names.
        return head.Contains((byte)0);
    }

    /// <summary>
    /// Determines whether a file is too large or too binary to be searched.
    /// </summary>
    /// <param name="fullPath">The absolute file path.</param>
    /// <param name="reason">A short explanation when the file is skipped.</param>
    /// <returns><see langword="true"/> when the file may be read as text.</returns>
    public static bool IsSearchableText(string fullPath, out string? reason)
    {
        reason = null;

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                reason = "missing";
                return false;
            }

            if (info.Length > MaxSearchableFileBytes)
            {
                reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"larger than {MaxSearchableFileBytes / 1024} KiB");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }

        byte[] head;
        try
        {
            head = ReadHead(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }

        if (LooksBinary(head))
        {
            reason = "binary";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a file's leading bytes without assuming its size.
    /// </summary>
    /// <param name="fullPath">The absolute file path.</param>
    /// <returns>The sample, which may be shorter than <see cref="BinarySniffBytes"/>.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
    public static byte[] ReadHead(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var buffer = new byte[BinarySniffBytes];
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = stream.ReadAtLeast(buffer, BinarySniffBytes, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }
}
