using System.Globalization;
using SharpCoreAgent.Domain;

namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// Walks a workspace once and reports every file worth an index row, in a stable order.
/// </summary>
/// <remarks>
/// The walk is deliberately boring: no globbing, no <c>.gitignore</c> parsing, no language detection beyond
/// the extension. What it does have to get right is the boundary — a directory that is enormous and generated
/// (<c>node_modules</c>, <c>bin</c>), this index's own database, a junction that loops back into the tree, or a
/// link that points outside the workspace must never contribute a row, because the cost of a wrong inclusion
/// is paid on every query afterwards.
/// <para>
/// Ordering is ordinal by relative path, which makes a build reproducible: the same tree produces the same
/// insert order, so the same index, so the same ranking.
/// </para>
/// </remarks>
internal sealed class WorkspaceFileScanner
{
    /// <summary>
    /// The directory names the walk never descends into.
    /// </summary>
    /// <remarks>
    /// It is the same set the workspace tools use, kept here as a value because <c>SharpCoreAgent.Indexing</c> may
    /// not reference <c>SharpCoreAgent.Infrastructure</c> (the dependency runs the other way). A test pins the two
    /// lists together so they cannot drift apart in silence.
    /// </remarks>
    internal static IReadOnlyList<string> IgnoredDirectoryNames { get; } =
        [".git", "node_modules", "bin", "obj", ".vs", ".sharpcoreagent"];

    /// <summary>The size above which a file is recorded as skipped instead of read, in bytes.</summary>
    internal const long MaxFileBytes = 1024 * 1024;

    /// <summary>How many leading bytes are examined to decide whether a file is binary.</summary>
    internal const int SniffBytes = 8 * 1024;

    private readonly Workspace _workspace;

    private WorkspaceFileScanner(Workspace workspace) => _workspace = workspace;

    /// <summary>
    /// Creates a scanner for one workspace.
    /// </summary>
    /// <param name="workspace">The workspace to walk.</param>
    /// <returns>The scanner; nothing is read until <see cref="Scan"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    public static WorkspaceFileScanner For(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new WorkspaceFileScanner(workspace);
    }

    /// <summary>
    /// Walks the workspace.
    /// </summary>
    /// <param name="cancellationToken">Cancels the walk between files.</param>
    /// <returns>Every file found, indexable and skipped alike, ordered by relative path.</returns>
    public IReadOnlyList<ScannedFile> Scan(CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var visitedDirectories = new HashSet<string>(StringComparer.FromComparison(comparison));
        var visitedFiles = new HashSet<string>(StringComparer.FromComparison(comparison));

        var found = new List<ScannedFile>();
        var pending = new Stack<string>();
        pending.Push(_workspace.Root);
        visitedDirectories.Add(_workspace.Root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();

            foreach (var child in EnumerateDirectories(directory))
            {
                if (IsIgnored(Path.GetFileName(child)))
                {
                    continue;
                }

                if (TryResolveLink(child, asDirectory: true, out _))
                {
                    // A linked directory is never walked under its link name. When the target is inside the
                    // workspace it is reached through its own name, and when it is outside it is out of bounds;
                    // either way walking it here would either duplicate the tree or index another repository.
                    continue;
                }

                if (!_workspace.IsInside(child) || !visitedDirectories.Add(child))
                {
                    continue;
                }

                pending.Push(child);
            }

            foreach (var file in EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                _ = TryResolveLink(file, asDirectory: false, out var real);

                if (!_workspace.IsInside(real) || !visitedFiles.Add(real))
                {
                    // A link that leaves the workspace is out of bounds, and the same bytes under two names are
                    // one row: the file is indexed once, under its real path.
                    continue;
                }

                found.Add(Describe(real));
            }
        }

        found.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return found;
    }

    private ScannedFile Describe(string fullPath)
    {
        var language = LanguageFor(Path.GetExtension(fullPath));

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return Skip(fullPath, language, 0, DateTimeOffset.MinValue, "missing");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Skip(fullPath, language, 0, DateTimeOffset.MinValue, Describe(ex));
        }

        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        if (info.Length > MaxFileBytes)
        {
            return Skip(
                fullPath,
                language,
                info.Length,
                modified,
                string.Create(CultureInfo.InvariantCulture, $"larger than {MaxFileBytes / 1024} KiB"));
        }

        try
        {
            if (LooksBinary(fullPath))
            {
                return Skip(fullPath, language, info.Length, modified, "binary");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Skip(fullPath, language, info.Length, modified, Describe(ex));
        }

        return new ScannedFile
        {
            Path = _workspace.ToRelative(fullPath),
            FullPath = fullPath,
            Language = language,
            SizeBytes = info.Length,
            ModifiedUtc = modified,
        };
    }

    private ScannedFile Skip(string fullPath, string language, long size, DateTimeOffset modified, string reason) =>
        new()
        {
            Path = _workspace.ToRelative(fullPath),
            FullPath = fullPath,
            Language = language,
            SizeBytes = size,
            ModifiedUtc = modified,
            SkipReason = reason,
        };

    private static bool LooksBinary(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[SniffBytes];
        var read = stream.ReadAtLeast(head, SniffBytes, throwOnEndOfStream: false);

        // A NUL byte in the first few kilobytes is the cheapest signal that this is not text. It also catches
        // UTF-16, which the tokeniser would otherwise read as one long word of nonsense.
        return head.AsSpan(0, read).Contains((byte)0);
    }

    private static IEnumerable<string> EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be listed contributes nothing rather than failing the build; the tool the
            // model uses for the same tree reports the access error, which is where a human can act on it.
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsIgnored(string name) =>
        IgnoredDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool TryResolveLink(string path, bool asDirectory, out string real)
    {
        real = path;

        try
        {
            FileSystemInfo info = asDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return false;
            }

            real = target.FullName;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Describe(Exception exception) =>
        string.Create(CultureInfo.InvariantCulture, $"unreadable: {exception.Message}");

    private static string LanguageFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" or ".csx" => "csharp",
        ".ts" or ".tsx" or ".mts" or ".cts" => "typescript",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
        ".py" => "python",
        ".go" => "go",
        ".rs" => "rust",
        ".java" => "java",
        ".kt" or ".kts" => "kotlin",
        ".c" => "c",
        ".h" or ".hpp" or ".hh" or ".cc" or ".cpp" or ".cxx" => "cpp",
        ".rb" => "ruby",
        ".php" => "php",
        ".swift" => "swift",
        ".sql" => "sql",
        ".sh" or ".bash" or ".zsh" => "shell",
        ".ps1" or ".psm1" or ".psd1" => "powershell",
        ".md" or ".markdown" => "markdown",
        ".json" or ".jsonc" => "json",
        ".yaml" or ".yml" => "yaml",
        ".toml" => "toml",
        ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".resx" => "xml",
        ".html" or ".htm" => "html",
        ".css" or ".scss" or ".sass" or ".less" => "css",
        ".razor" => "razor",
        _ => "text",
    };
}
