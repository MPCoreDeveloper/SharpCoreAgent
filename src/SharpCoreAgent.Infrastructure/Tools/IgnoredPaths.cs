namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The names no traversal may descend into.
/// </summary>
/// <remarks>
/// One list, shared by <c>list_directory</c> and <c>search_text</c>, because a directory the model can
/// see but not search (or vice versa) is worse than either rule alone. The list is the usual culprits:
/// version-control metadata, installed packages, build output and this tool's own session store, all
/// of which are enormous, generated and irrelevant to the question being asked.
/// <para>
/// It is exposed as a value rather than a predicate so a test can assert the exact set.
/// </para>
/// </remarks>
public static class IgnoredPaths
{
    /// <summary>Gets the directory names skipped during traversal, matched case-insensitively.</summary>
    public static IReadOnlyList<string> DirectoryNames { get; } =
        [".git", "node_modules", "bin", "obj", ".vs", ".sharpcoreagent"];

    /// <summary>
    /// Determines whether a directory name is skipped.
    /// </summary>
    /// <param name="name">The directory's own name, not its path.</param>
    /// <returns><see langword="true"/> when traversal must not descend into it.</returns>
    public static bool IsIgnored(string name) =>
        name is not null && DirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
