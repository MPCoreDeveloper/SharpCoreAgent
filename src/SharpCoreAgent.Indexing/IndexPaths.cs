using SharpCoreAgent.Domain;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The four paths the index owns, all of them under <c>&lt;workspace&gt;/.sharpcoreagent/index/</c>.
/// </summary>
/// <remarks>
/// Every path is derived from <see cref="Workspace.Root"/> once, so nothing else in this assembly builds a
/// path by string concatenation and no caller can point the index at a directory outside the workspace it
/// was created for. <c>.sharpcoreagent</c> is already an ignored path for the workspace tools, so the index can
/// never index its own database and never shows up in a search.
/// </remarks>
internal sealed class IndexPaths
{
    private const string MetadataDirectoryName = ".sharpcoreagent";
    private const string IndexDirectoryName = "index";

    // SharpCoreDB appends ".scdb" to any path that does not already end with it, so the name carries the
    // extension: what this class computes is exactly the file on disk, not a prefix of it.
    private const string DatabaseFileName = "sharpcoreagent.scdb";
    private const string LockFileName = ".lock";
    private const string RebuildFileName = "sharpcoreagent.rebuild.scdb";

    private IndexPaths(string workspaceRoot, string indexDirectory)
    {
        WorkspaceRoot = workspaceRoot;
        IndexDirectory = indexDirectory;
        DatabasePath = Path.Combine(indexDirectory, DatabaseFileName);
        LockPath = Path.Combine(indexDirectory, LockFileName);
        RebuildPath = Path.Combine(indexDirectory, RebuildFileName);
    }

    /// <summary>Gets the absolute workspace root the index belongs to.</summary>
    public string WorkspaceRoot { get; }

    /// <summary>Gets the absolute directory that holds every index file.</summary>
    public string IndexDirectory { get; }

    /// <summary>Gets the absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets the absolute path of the zero-byte writer lock.</summary>
    public string LockPath { get; }

    /// <summary>Gets the absolute path a rebuild writes to before it replaces the live database.</summary>
    public string RebuildPath { get; }

    /// <summary>Gets a value indicating whether the database file exists.</summary>
    public bool DatabaseExists => File.Exists(DatabasePath);

    /// <summary>
    /// Computes the paths for one workspace.
    /// </summary>
    /// <param name="workspace">The workspace whose root owns the index.</param>
    /// <returns>The path set; nothing is created on disk.</returns>
    public static IndexPaths For(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var indexDirectory = Path.Combine(workspace.Root, MetadataDirectoryName, IndexDirectoryName);
        return new IndexPaths(workspace.Root, indexDirectory);
    }

    /// <summary>
    /// Creates the index directory when it is absent.
    /// </summary>
    public void EnsureDirectoryCreated() => Directory.CreateDirectory(IndexDirectory);

    /// <summary>
    /// Renders the workspace root the way <c>meta['workspace_root']</c> stores it: absolute, with forward
    /// slashes, so a database written on Windows still compares equal after it is copied to Linux.
    /// </summary>
    /// <returns>The comparable root.</returns>
    public string ComparableWorkspaceRoot => WorkspaceRoot.Replace('\\', '/');
}
