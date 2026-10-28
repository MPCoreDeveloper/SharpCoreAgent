using System.Globalization;
using SharpCoreAgent.Domain;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Turns a model-supplied path into an absolute path that is provably inside the workspace.
/// </summary>
/// <remarks>
/// <see cref="Workspace.TryResolve(string, out string, out string?)"/> answers the lexical question and
/// nothing else; this type answers the physical one. A symbolic link or junction that lives inside the
/// root can still point outside it, so the deepest existing component of the requested path is
/// resolved to its final target before the path is handed to the file system.
/// <para>
/// A path that does not exist yet is legal — <c>write_file</c> has to create files — which is why the
/// check walks up to the nearest existing ancestor instead of requiring the target to be there.
/// </para>
/// </remarks>
public static class PathSafety
{
    private const int MaxLinkDepth = 64;

    /// <summary>
    /// Resolves a workspace-relative path and rejects one whose physical target escapes the root.
    /// </summary>
    /// <param name="workspace">The workspace the invocation may touch.</param>
    /// <param name="path">The model's path argument.</param>
    /// <param name="fullPath">The absolute path when the call succeeds.</param>
    /// <param name="error">A message naming the escape when the call fails.</param>
    /// <returns><see langword="true"/> when the path is safe to use.</returns>
    public static bool TryResolve(Workspace workspace, string path, out string fullPath, out string? error)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!workspace.TryResolve(path, out var lexical, out error))
        {
            fullPath = string.Empty;
            return false;
        }

        var judged = JudgePhysical(workspace, path, lexical, allowAttachments: false, out error);
        fullPath = judged ?? string.Empty;
        return judged is not null;
    }

    /// <summary>
    /// Resolves a path a read tool may use, rejecting one whose physical target escapes every root.
    /// </summary>
    /// <param name="workspace">The workspace the invocation may read from.</param>
    /// <param name="path">The model's path argument.</param>
    /// <param name="fullPath">The absolute path when the call succeeds.</param>
    /// <param name="owner">The attachment that answered, or <see langword="null"/> when the primary root did.</param>
    /// <param name="error">A message naming the escape when the call fails.</param>
    /// <returns><see langword="true"/> when the path is safe to read.</returns>
    /// <remarks>
    /// The only difference from <see cref="TryResolve(Workspace, string, out string, out string?)"/> is which set
    /// of roots the physical target is compared against, so a link inside an attachment that points into the
    /// primary root is accepted and one that points anywhere else is refused. Link resolution, the depth cap and
    /// the "an unreadable link is judged lexically" rule are the same code, which is why a future fix to link
    /// handling cannot fix one path and miss the other.
    /// </remarks>
    public static bool TryResolveRead(
        Workspace workspace,
        string path,
        out string fullPath,
        out Workspace? owner,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!workspace.TryResolveRead(path, out var lexical, out owner, out error))
        {
            fullPath = string.Empty;
            return false;
        }

        var judged = JudgePhysical(workspace, path, lexical, allowAttachments: true, out error);
        fullPath = judged ?? string.Empty;
        if (judged is null)
        {
            owner = null;
        }

        return judged is not null;
    }

    /// <summary>
    /// Completes a write refusal with the reason, when the path belongs to a read-only attachment.
    /// </summary>
    /// <param name="workspace">The workspace the write was resolved through.</param>
    /// <param name="path">The path argument the tool refused.</param>
    /// <param name="error">The refusal the resolver already produced.</param>
    /// <returns>
    /// <paramref name="error"/> unchanged for an ordinary escape, or that message plus the sentence naming
    /// the attachment that would have allowed a read.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> or <paramref name="error"/> is null.</exception>
    /// <remarks>
    /// One helper for the three write tools, because a refusal that names the reason is the difference between
    /// "the tool is broken" and "attach it and read it instead" — and three copies of that sentence would
    /// eventually disagree about its wording.
    /// </remarks>
    public static string ExplainWriteRefusal(Workspace workspace, string path, string error)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(error);

        var hint = workspace.DescribeOutside(path);
        return hint is null
            ? error
            : string.Create(CultureInfo.InvariantCulture, $"{error}. {hint}");
    }

    /// <summary>
    /// Answers the physical question for a path the workspace already accepted lexically.
    /// </summary>
    /// <param name="workspace">The workspace whose containment rules apply.</param>
    /// <param name="path">The model's path argument, for the message.</param>
    /// <param name="fullPath">The lexically accepted absolute path.</param>
    /// <param name="allowAttachments">Whether a link may land in an attachment.</param>
    /// <param name="error">A message naming the escape when the call fails.</param>
    /// <returns>The path to hand back when it is safe, or <see langword="null"/> after filling <paramref name="error"/>.</returns>
    private static string? JudgePhysical(
        Workspace workspace,
        string path,
        string fullPath,
        bool allowAttachments,
        out string? error)
    {
        var physical = ResolvePhysical(fullPath);
        if (physical is null ||
            (allowAttachments ? workspace.IsInsideAny(physical) : workspace.IsInside(physical)))
        {
            error = null;
            return fullPath;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"path '{path}' resolves through a link to '{physical}', which is outside the workspace root '{workspace.Root}'");
        return null;
    }

    /// <summary>
    /// Finds the physical location of a path whose last components may not exist.
    /// </summary>
    /// <param name="fullPath">An absolute path, already known to be lexically inside the workspace.</param>
    /// <returns>The real path of the deepest existing component, or <see langword="null"/> when none exists.</returns>
    private static string? ResolvePhysical(string fullPath)
    {
        var current = fullPath;

        for (var depth = 0; depth < MaxLinkDepth; depth++)
        {
            if (Exists(current))
            {
                return ResolveLink(current);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a link chain to its final target, tolerating a link whose target is missing.
    /// </summary>
    private static string ResolveLink(string path)
    {
        try
        {
            var info = Directory.Exists(path)
                ? (FileSystemInfo)new DirectoryInfo(path)
                : new FileInfo(path);

            if (info.LinkTarget is null)
            {
                return path;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable link cannot be proven safe, so it is judged by its lexical location and
            // the file system will reject the operation itself if it is not.
            return path;
        }
    }
}
