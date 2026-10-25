using System.Globalization;

namespace SharpCoreAgent.Domain;

/// <summary>
/// The single directory tree an agent turn may read from or write to.
/// </summary>
/// <remarks>
/// A workspace is a value, not a session: it normalises one root path at construction time and then
/// answers containment questions the same way for as long as the process lives. Every tool asks the
/// workspace instead of comparing path strings itself, which is what makes "no tool may escape the
/// root" a property of the model rather than a rule each tool has to remember.
/// <para>
/// Containment is decided after <see cref="Path.GetFullPath(string)"/>, so <c>..\..\etc\passwd</c>
/// is rejected before a tool ever asks the file system about it. Symbolic links and junctions are
/// resolved separately by the tools, because a link inside the root can still point outside it.
/// </para>
/// </remarks>
public sealed class Workspace
{
    private readonly StringComparison _comparison;

    private Workspace(string root, StringComparison comparison)
    {
        Root = root;
        _comparison = comparison;
    }

    /// <summary>
    /// Gets the absolute, normalised, separator-trimmed workspace root.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Creates a workspace for an existing directory.
    /// </summary>
    /// <param name="root">An absolute or relative directory path.</param>
    /// <returns>The normalised workspace.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, empty or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static Workspace Create(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DirectoryNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"Workspace root '{root}' is not a valid path: {ex.Message}"), ex);
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"Workspace root '{full}' does not exist."));
        }

        // Windows file systems are case-insensitive by default; POSIX ones are not. Following the
        // platform is the only choice that neither rejects a legitimate path nor accepts a sibling
        // directory whose name differs only in case.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return new Workspace(TrimTrailingSeparator(full), comparison);
    }

    /// <summary>
    /// Resolves a caller-supplied path against the root without touching the file system's link layer.
    /// </summary>
    /// <param name="path">A relative path, or an absolute path that must already be inside the root.</param>
    /// <param name="fullPath">The normalised absolute path when the call succeeds.</param>
    /// <param name="error">A human-readable reason when the call fails; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> stays inside the root.</returns>
    public bool TryResolve(string path, out string fullPath, out string? error)
    {
        fullPath = string.Empty;

        if (path is null)
        {
            error = "path is required";
            return false;
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            error = "path contains a NUL character";
            return false;
        }

        string combined;
        try
        {
            // Path.Combine returns the second argument unchanged when it is rooted, so an absolute
            // path is accepted here and then judged by the containment test below.
            combined = Path.GetFullPath(Path.Combine(Root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"path '{path}' is not a valid path: {ex.Message}");
            return false;
        }

        if (!IsInside(combined))
        {
            error = string.Create(CultureInfo.InvariantCulture, $"path '{path}' resolves outside the workspace root '{Root}'");
            return false;
        }

        fullPath = combined;
        error = null;
        return true;
    }

    /// <summary>
    /// Determines whether an absolute path is the root itself or lives underneath it.
    /// </summary>
    /// <param name="fullPath">An absolute path.</param>
    /// <returns><see langword="true"/> when the path is contained by this workspace.</returns>
    public bool IsInside(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        if (string.Equals(fullPath, Root, _comparison))
        {
            return true;
        }

        var prefix = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, _comparison);
    }

    /// <summary>
    /// Renders an absolute path the way tool output shows it: forward slashes, relative to the root.
    /// </summary>
    /// <param name="fullPath">An absolute path, normally one produced by <see cref="TryResolve(string, out string, out string?)"/>.</param>
    /// <returns>A relative path with <c>/</c> separators, or the input when it is outside the root.</returns>
    public string ToRelative(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        if (!IsInside(fullPath))
        {
            return fullPath;
        }

        if (string.Equals(fullPath, Root, _comparison))
        {
            return ".";
        }

        return Path.GetRelativePath(Root, fullPath).Replace('\\', '/');
    }

    /// <inheritdoc />
    public override string ToString() => Root;

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        if (path[^1] != Path.DirectorySeparatorChar && path[^1] != Path.AltDirectorySeparatorChar)
        {
            return path;
        }

        // "C:\" is a root, not a directory called "C:" -- trimming it would change the meaning.
        if (path.Length == 3 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return path;
        }

        return path[..^1];
    }
}
