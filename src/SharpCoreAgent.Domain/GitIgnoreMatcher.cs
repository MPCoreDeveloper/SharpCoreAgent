using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SharpCoreAgent.Domain;

/// <summary>
/// The <c>.gitignore</c> rules that apply to one workspace tree.
/// </summary>
/// <remarks>
/// Git's ignore syntax is small but not trivial: a nested file applies only below its directory, later rules beat
/// earlier ones, <c>!</c> unignores, a trailing slash means directory-only, and <c>**</c> crosses separators. The
/// matcher keeps the files in the order they were loaded so the last matching rule is the answer, exactly as Git
/// applies the same file from top to bottom.
/// <para>
/// Only <c>.gitignore</c> files inside the workspace root are read. The user's global excludes and
/// <c>.git/info/exclude</c> are deliberately not consulted: scanning policy must be the same on every machine and in
/// every CI image, and a developer's personal exclude should not silently make a file invisible to the agent.
/// </para>
/// <para>
/// The matcher reads files and changes nothing. A scanner calls <see cref="LoadFrom"/> when it enters a directory;
/// a directory that is already ignored is never entered, so a nested ignore file below an ignored parent is not
/// loaded, which is Git's own traversal rule.
/// </para>
/// </remarks>
public sealed class GitIgnoreMatcher
{
    private readonly string _root;
    private readonly List<GitIgnoreRule> _rules = [];
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private GitIgnoreMatcher(string root) => _root = root;

    /// <summary>Creates a matcher for a workspace root and loads its root <c>.gitignore</c>.</summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <returns>The matcher.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    public static GitIgnoreMatcher For(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var matcher = new GitIgnoreMatcher(Path.GetFullPath(root));
        matcher.LoadFrom(matcher._root);
        return matcher;
    }

    /// <summary>Loads the <c>.gitignore</c> in one directory, if it exists and has not been loaded.</summary>
    /// <param name="directory">The absolute directory to read.</param>
    /// <remarks>
    /// A missing, unreadable or malformed file contributes no rules; scanning is a read-only convenience and must not
    /// fail because one ignore file is unreadable. A file is loaded at most once.
    /// </remarks>
    public void LoadFrom(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var full = Path.GetFullPath(directory);
        if (!IsInside(full) || !Directory.Exists(full))
        {
            return;
        }

        var key = Path.GetRelativePath(_root, full);
        if (!_loaded.Add(key))
        {
            return;
        }

        var file = Path.Combine(full, ".gitignore");
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists || info.LinkTarget is not null)
            {
                // A linked ignore file is not read: its target may be outside the workspace, and Git does not use a
                // symlinked .gitignore as a rule file.
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (GitIgnoreRule.TryParse(line, key, out var rule))
                {
                    _rules.Add(rule);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable ignore file leaves the tree visible, which is the safe default for a read-only scan.
        }
    }

    /// <summary>Loads every ignore file from the root through the path's directory.</summary>
    /// <param name="path">A file or directory the scanner is about to inspect.</param>
    /// <remarks>
    /// A search can start below the root. Without this, a <c>build/</c> rule in the root would be forgotten when the
    /// search starts in <c>src</c>.
    /// </remarks>
    public void LoadAncestors(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        var ancestors = new Stack<string>();
        while (!string.IsNullOrEmpty(directory) && IsInside(directory))
        {
            ancestors.Push(directory);
            if (string.Equals(directory, _root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        // Root first, then each nested directory: later files must win over the root rules, exactly as Git applies
        // a nested file after the file that contains its parent.
        while (ancestors.Count > 0)
        {
            LoadFrom(ancestors.Pop());
        }
    }

    /// <summary>Determines whether a path is ignored by the rules loaded so far.</summary>
    /// <param name="fullPath">The absolute path to judge.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    /// <returns><see langword="true"/> when the last matching rule ignores the path.</returns>
    public bool IsIgnored(string fullPath, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var relative = Path.GetRelativePath(_root, Path.GetFullPath(fullPath)).Replace('\\', '/');
        if (relative == "." || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        var current = relative;
        var currentIsDirectory = isDirectory;
        while (true)
        {
            if (Matches(current, currentIsDirectory))
            {
                return true;
            }

            var slash = current.LastIndexOf('/');
            if (slash < 0)
            {
                return false;
            }

            // A rule that ignores an ancestor hides its children; Git cannot re-include a child below an ignored
            // directory from a file the traversal never read.
            current = current[..slash];
            currentIsDirectory = true;
        }
    }

    private bool Matches(string relative, bool isDirectory)
    {
        try
        {
            var matches = _rules.Where(rule => rule.Matches(relative, isDirectory)).ToList();
            return matches.Count > 0 && !matches[^1].Negated;
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological rule must not make a read-only scan hang or fail.
            return false;
        }
    }

    private bool IsInside(string path) =>
        string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(_root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
