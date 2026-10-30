using SharpCoreAgent.Domain;

namespace SharpCoreAgent.Languages.FSharp;

/// <summary>
/// Finds the single F# project a workspace holds, once, and remembers that it looked.
/// </summary>
/// <remarks>
/// Called on the first code-intelligence request of a session and never again for the same workspace, because the
/// answer cannot change inside one process without the developer editing a project file — and a probe that
/// re-walked a large tree on every tool call would make the cheap tool the expensive one.
/// <para>
/// It never throws. A directory the process may not read, a junction that resolves to nowhere and a tree with no
/// F# in it all answer <see langword="null"/>, which the adapter turns into a failure whose hint names a tool the
/// model can actually call. The alternative — an exception escaping a read-only probe — would report a permissions
/// problem as a crash in the middle of a turn.
/// </para>
/// </remarks>
/// <param name="workspace">The workspace whose primary root is searched.</param>
internal sealed class FSharpProjectProbe(Workspace workspace)
{
    private static readonly string[] BuildFolders = ["bin", "obj", ".git", ".sharpcoreagent", "node_modules", ".vs"];

    private readonly Lock _gate = new();
    private string? _project;
    private bool _probed;

    /// <summary>
    /// Gets the absolute path of the project the workspace holds, or <see langword="null"/> when it holds none.
    /// </summary>
    /// <returns>The memoised answer; the file system is read at most once per instance.</returns>
    public string? Find()
    {
        lock (_gate)
        {
            if (!_probed)
            {
                _project = Discover();
                _probed = true;
            }

            return _project;
        }
    }

    /// <summary>
    /// Forgets the memoised answer, so a project file created during a session is found by the next call.
    /// </summary>
    /// <remarks>
    /// Only a test needs this: a session that writes a new <c>.fsproj</c> is a session whose next turn is a new
    /// process. Keeping the seam here rather than in the adapter means the adapter has no mutable state to reset.
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
        {
            _probed = false;
            _project = null;
        }
    }

    private string? Discover()
    {
        try
        {
            // Shallowest first, then alphabetical: a solution root's own project beats a sample nested under
            // `tests/`, and the tie-break is stable so two runs on the same tree answer the same path.
            return Directory.EnumerateFiles(workspace.Root, "*.fsproj", SearchOption.AllDirectories)
                .Where(IsNotBuildOutput)
                .OrderBy(Depth)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private int Depth(string path) =>
        Path.GetRelativePath(workspace.Root, path).Count(IsSeparator);

    private static bool IsSeparator(char character) =>
        character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;

    private static bool IsNotBuildOutput(string path)
    {
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        var segments = path.Split(separators);
        return !segments.Any(segment => BuildFolders.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
