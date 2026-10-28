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
/// <para>
/// A workspace may also carry read-only <em>attachments</em>: other directories the read tools may answer
/// from and no tool may write to. They are deliberately not a second root — <see cref="TryResolve"/> never
/// consults them — which is what keeps "no write escapes the root" a property of one path rather than of
/// every call site. <see cref="TryResolveRead"/> is the read-only counterpart, and it names the attachment
/// that answered so a tool can show a path relative to the tree it came from.
/// </para>
/// </remarks>
public sealed class Workspace
{
    private static readonly Workspace[] NoAttachments = [];

    private readonly StringComparison _comparison;
    private readonly Workspace[] _attachments;

    private Workspace(string root, StringComparison comparison, Workspace[]? attachments = null)
    {
        Root = root;
        _comparison = comparison;
        _attachments = attachments ?? NoAttachments;
    }

    /// <summary>
    /// Gets the absolute, normalised, separator-trimmed workspace root.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Gets the read-only roots attached to this workspace, in the order they were named.
    /// </summary>
    /// <remarks>
    /// Each attachment is itself a <see cref="Workspace"/>, because containment is the prefix comparison this
    /// type already performs: an attachment is asked whether it contains a path exactly as the primary root
    /// is, so there is one implementation of "inside" rather than two that can drift apart.
    /// </remarks>
    public IReadOnlyList<Workspace> Attachments => _attachments;

    /// <summary>
    /// Gets a value indicating whether this workspace has at least one read-only attachment.
    /// </summary>
    public bool HasAttachments => _attachments.Length > 0;

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
    /// Returns a new workspace carrying these roots as read-only attachments.
    /// </summary>
    /// <param name="roots">Directory paths, absolute or relative to the process's working directory.</param>
    /// <returns>A new workspace; this value is unchanged, because a workspace is an immutable fact.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// A root does not exist, is a file rather than a directory, or cannot be normalised.
    /// </exception>
    /// <remarks>
    /// Blank entries are ignored, which is what lets an environment variable split on
    /// <see cref="Path.PathSeparator"/> produce a harmless empty string. A root that equals the primary root,
    /// sits underneath it, repeats one already attached, or is nested under another attachment is dropped
    /// rather than refused: the outer root already grants the same read, and a usage error here would
    /// punish a developer for naming the directory they meant twice.
    /// <para>
    /// Validation is <see cref="Create"/>'s, so an attachment root cannot be normalised differently from the
    /// primary root. Only the wording differs, because with several roots in play the message has to say
    /// which kind of root it is about.
    /// </para>
    /// </remarks>
    public Workspace WithAttachments(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var kept = new List<Workspace>(_attachments);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = CreateAttachment(root.Trim());

            // The primary root and any outer attachment already grant this read.
            if (IsInside(candidate.Root) || kept.Any(attachment => attachment.IsInside(candidate.Root)))
            {
                continue;
            }

            // Named the other way round: this root contains attachments already listed, so it supersedes
            // them and they are dropped instead. Order is otherwise preserved.
            kept.RemoveAll(attachment => candidate.IsInside(attachment.Root));
            kept.Add(candidate);
        }

        return kept.Count == _attachments.Length && kept.SequenceEqual(_attachments)
            ? this
            : new Workspace(Root, _comparison, [.. kept]);
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
    /// Resolves a path the way a <em>read</em> tool may: the primary root or any attachment, never a write.
    /// </summary>
    /// <param name="path">A relative path, or an absolute path inside the primary root or an attachment.</param>
    /// <param name="fullPath">The normalised absolute path when the call succeeds.</param>
    /// <param name="owner">
    /// The attachment that answered, or <see langword="null"/> when the primary root did. A tool uses it to
    /// show a path relative to the tree it came from instead of an absolute path.
    /// </param>
    /// <param name="error">A human-readable reason when the call fails; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> stays inside one of the roots this read may use.</returns>
    /// <remarks>
    /// A relative path is answered by the primary root and only by it, so two repositories that both hold
    /// <c>src/Order.cs</c> cannot make one argument mean whichever root was attached first. An absolute path
    /// is answered by the first root that contains it, which is why <c>list_roots</c> exists: the model is
    /// meant to read the absolute path rather than guess one.
    /// </remarks>
    public bool TryResolveRead(string path, out string fullPath, out Workspace? owner, out string? error)
    {
        fullPath = string.Empty;
        owner = null;

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

        if (!Path.IsPathRooted(path))
        {
            // The write path's own resolution, so a relative path means the same file with and without
            // attachments and its refusals keep the wording the suite already pins.
            return TryResolve(path, out fullPath, out error);
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"path '{path}' is not a valid path: {ex.Message}");
            return false;
        }

        if (IsInside(combined))
        {
            fullPath = combined;
            error = null;
            return true;
        }

        var match = _attachments.FirstOrDefault(attachment => attachment.IsInside(combined));
        if (match is not null)
        {
            fullPath = combined;
            owner = match;
            error = null;
            return true;
        }

        if (_attachments.Length == 0)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"path '{path}' resolves outside the workspace root '{Root}'");
            return false;
        }

        var noun = _attachments.Length == 1 ? "attachment" : "attachments";
        error = string.Create(
            CultureInfo.InvariantCulture,
            $"path '{path}' is outside the workspace root '{Root}' and its {_attachments.Length} read-only {noun}. list_roots names the roots a path may use.");
        return false;
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
    /// Determines whether an absolute path is inside this workspace root or any attachment.
    /// </summary>
    /// <param name="fullPath">An absolute path, normally one a link was resolved to.</param>
    /// <returns><see langword="true"/> when some root this workspace holds contains the path.</returns>
    /// <remarks>
    /// This is the physical check's question, not a write's: attaching a directory must not create a link
    /// that escapes every root, and a link that lands in the primary root is still inside what the developer
    /// granted. <see cref="IsInside"/> keeps meaning "the primary root", because that is the write promise.
    /// </remarks>
    public bool IsInsideAny(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        if (IsInside(fullPath))
        {
            return true;
        }

        return _attachments.Any(attachment => attachment.IsInside(fullPath));
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

    /// <summary>
    /// Explains a refusal that happened only because the path belongs to a read-only attachment.
    /// </summary>
    /// <param name="path">The path argument a write tool was given, relative or absolute.</param>
    /// <returns>
    /// One sentence naming the attachment, or <see langword="null"/> when the path is inside no attachment —
    /// an ordinary escape, which the caller's existing message already describes.
    /// </returns>
    /// <remarks>
    /// This is an enrichment rather than a re-wording on purpose: <see cref="TryResolve"/>'s message is pinned
    /// by the existing suite and promised by the contract, so a write tool appends this sentence to it instead
    /// of changing what was already there. "Attach it and read it instead" is the difference between a tool
    /// that reads as broken and one that reads as a decision.
    /// </remarks>
    public string? DescribeOutside(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(Root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (IsInside(combined))
        {
            return null;
        }

        var match = _attachments.FirstOrDefault(attachment => attachment.IsInside(combined));
        return match is null
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"'{match.Root}' is a read-only attachment; writes stay in the primary root.");
    }

    /// <inheritdoc />
    public override string ToString() => Root;

    /// <summary>
    /// Normalises an attachment root the way <see cref="Create"/> normalises the primary one.
    /// </summary>
    /// <param name="root">The path the developer named.</param>
    /// <returns>The attachment workspace.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The path is not an existing directory, or cannot be normalised.
    /// </exception>
    /// <remarks>
    /// The existence and normalisation rules are <see cref="Create"/>'s, re-entered rather than copied; only the
    /// message names the root as an attachment, because several roots are in play by the time one is wrong.
    /// </remarks>
    private static Workspace CreateAttachment(string root)
    {
        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DirectoryNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"attachment root '{root}' is not a valid path: {ex.Message}"), ex);
        }

        if (File.Exists(full))
        {
            throw new DirectoryNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"attachment root '{full}' is a file, not a directory."));
        }

        try
        {
            return Create(full);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new DirectoryNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"attachment root '{full}' does not exist."), ex);
        }
    }

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
