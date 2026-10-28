using System.Globalization;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// Enforces the tool-result budget by shortening the text the model will read.
/// </summary>
/// <remarks>
/// Truncation keeps the head and says how much was dropped. Dropping the tail silently would make the
/// model believe it saw the whole file, which is worse than being told the answer is incomplete.
/// </remarks>
public static class ToolResultTrimmer
{
    private const string OmittedMarker = "\n[sharpcoreagent: truncated]";

    /// <summary>
    /// Shortens a result's content to the budget.
    /// </summary>
    /// <param name="result">The result to adjust.</param>
    /// <param name="maxChars">The largest allowed content length; a non-positive value disables trimming.</param>
    /// <returns>The original result, or a copy with shortened content and <see cref="ToolResult.IsTruncated"/> set.</returns>
    /// <remarks>
    /// A budget smaller than the marker cannot be honoured: the marker and the count line are longer
    /// than that, and telling the model the answer is incomplete is worth the extra characters.
    /// </remarks>
    public static ToolResult Trim(ToolResult result, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (maxChars <= 0 || result.Content.Length <= maxChars)
        {
            return result;
        }

        // Leave room for the marker and for the count line: both are read by the model, both count
        // against the budget, and the count line's own length depends on how much is dropped. The cut
        // is therefore found by shortening until the whole message fits.
        var keep = maxChars;
        string content;
        while (true)
        {
            var omitted = result.Content.Length - keep;
            content = string.Create(
                CultureInfo.InvariantCulture,
                $"{result.Content[..keep]}{OmittedMarker} {omitted} of {result.Content.Length} characters omitted.");
            if (content.Length <= maxChars || keep == 0)
            {
                break;
            }

            keep = Math.Max(0, keep - (content.Length - maxChars));
        }

        return result with { Content = content, IsTruncated = true };
    }
}
