using System.Globalization;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// How the index writes and reads a timestamp column: ISO-8601 UTC, round-trip exact, culture-free.
/// </summary>
/// <remarks>
/// The database stores timestamps as text, so a comparison between "the timestamp on disk" and "the timestamp
/// the scanner reported" is a string comparison. A format that drops sub-second digits or uses the ambient
/// culture would make the same file look changed on every build, which is exactly the failure the incremental
/// path exists to avoid.
/// </remarks>
internal static class IndexTimestamps
{
    /// <summary>
    /// Formats a timestamp for a <c>meta</c> or <c>files</c> column.
    /// </summary>
    /// <param name="value">The moment.</param>
    /// <returns>ISO-8601 in UTC, with seven fractional digits and an explicit offset.</returns>
    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a stored timestamp.
    /// </summary>
    /// <param name="text">The stored text.</param>
    /// <returns>The moment, or <see langword="null"/> when the text is empty or not a timestamp.</returns>
    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }
}
