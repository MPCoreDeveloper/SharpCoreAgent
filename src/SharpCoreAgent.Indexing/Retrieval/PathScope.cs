namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// One comparison shared by every path filter: is this path the prefix itself, or beneath it?
/// </summary>
/// <remarks>
/// The separator is appended before the prefix test, so <c>docs</c> names the directory and not a sibling that merely
/// starts with the same letters (<c>docs-generated/x</c>). The comparison is ordinal, matching the stored
/// <c>/</c>-separated form, so two runs of one build filter identically.
/// </remarks>
internal static class PathScope
{
    /// <summary>
    /// Reports whether a path is the prefix itself or sits beneath it.
    /// </summary>
    /// <param name="path">A workspace-relative path with <c>/</c> separators.</param>
    /// <param name="prefix">A normalised workspace-relative prefix.</param>
    /// <returns><see langword="true"/> when the path is the prefix or a descendant of it.</returns>
    internal static bool Contains(string path, string prefix) =>
        string.Equals(path, prefix, StringComparison.Ordinal)
        || path.StartsWith(prefix + "/", StringComparison.Ordinal);
}
