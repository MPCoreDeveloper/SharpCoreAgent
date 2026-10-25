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

        if (!workspace.TryResolve(path, out fullPath, out error))
        {
            return false;
        }

        var physical = ResolvePhysical(fullPath);
        if (physical is null || workspace.IsInside(physical))
        {
            error = null;
            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"path '{path}' resolves through a link to '{physical}', which is outside the workspace root '{workspace.Root}'");
        fullPath = string.Empty;
        return false;
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
