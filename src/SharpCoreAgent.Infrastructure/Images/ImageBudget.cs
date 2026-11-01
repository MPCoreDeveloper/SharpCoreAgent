using System.Globalization;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// What one turn may still spend on pictures: one predicate and one sentence, shared by every door that attaches one.
/// </summary>
/// <remarks>
/// Three tools can attach an image — <c>view_image</c>, a tool reached through an MCP server, and <c>fetch_url</c> — and
/// each of them already caps what it alone returns: one picture per call, four per result, four mebibytes each. None of
/// them can see the turn, and the turn is what a provider charges for, because an attached picture is re-sent on every
/// request that follows it. A turn that shows four pictures on each of fifteen rounds pays for sixty of them in every
/// request after the last one, and a request past the provider's own size limit fails for a reason no message explains.
/// <para>
/// The loop is the only place that sees a whole turn, so the loop counts what travelled and hands each tool what is
/// left; this type is where the arithmetic and the words live, so the three doors cannot disagree about either. A null
/// remaining budget means the caller stated none — a context built by hand in a test, a host that never filled it in —
/// and is refused by nothing, which is the same safe default <c>ToolInvocationContext.Model</c> takes for the
/// capability gate.
/// </para>
/// </remarks>
public static class ImageBudget
{
    /// <summary>
    /// Determines whether a picture fits in what the turn has left.
    /// </summary>
    /// <param name="bytes">The picture's size before base64, in bytes.</param>
    /// <param name="remaining">What the turn may still attach, or <see langword="null"/> when nobody said.</param>
    /// <returns><see langword="true"/> when the picture may travel.</returns>
    public static bool Fits(long bytes, long? remaining) => remaining is not { } left || bytes <= left;

    /// <summary>
    /// Explains why a picture did not fit, in the words all three doors use.
    /// </summary>
    /// <param name="bytes">The picture's size before base64, in bytes.</param>
    /// <param name="remaining">What the turn had left when the picture arrived.</param>
    /// <returns>One clause naming both numbers and the way out of the ceiling.</returns>
    /// <remarks>
    /// Both numbers, because either one alone leaves the model guessing: a ceiling with no size reads as a refusal to
    /// show anything, and a size with no ceiling reads as a bug. The way out is named because the ceiling is per turn —
    /// asking again in the same turn cannot work, and a model that tries anyway spends iterations learning nothing.
    /// </remarks>
    public static string Spent(long bytes, long remaining) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"this turn has {CodeIndexFormat.Size(Math.Max(0, remaining))} of image budget left and the picture is {CodeIndexFormat.Size(bytes)}; a new turn starts with a fresh budget");
}
