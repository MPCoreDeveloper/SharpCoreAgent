using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The tables an index test writes to, in the shape the production code writes them.
/// </summary>
/// <remarks>
/// Every write goes through <see cref="IndexHandleCache"/> rather than opening the file directly: the engine
/// keeps a handle on a single-file database, and a test that opens a second one is testing a situation the
/// product never creates.
/// </remarks>
internal static class IndexTestDatabase
{
    /// <summary>Creates the schema and every seed row.</summary>
    /// <param name="paths">The index to create.</param>
    public static void Create(IndexPaths paths)
    {
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: true);
        IndexSchema.EnsureCreated(lease.Database.Database, paths);
    }

    /// <summary>Runs one statement against an existing index, creating it when necessary.</summary>
    /// <param name="paths">The index to write to.</param>
    /// <param name="sql">The statement.</param>
    public static void Execute(IndexPaths paths, string sql)
    {
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: true);
        IndexSchema.EnsureCreated(lease.Database.Database, paths);
        lease.Database.Database.ExecuteSQL(sql);
    }

    /// <summary>Sets one <c>meta</c> key.</summary>
    /// <param name="paths">The index to write to.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">Its new value.</param>
    public static void SetMeta(IndexPaths paths, string key, string value) =>
        Execute(
            paths,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"UPDATE meta SET value = '{value.Replace("'", "''", StringComparison.Ordinal)}' WHERE key = '{key}'"));

    /// <summary>Marks the index as the product of one complete, empty build.</summary>
    /// <param name="paths">The index to write to.</param>
    public static void CompleteBuild(IndexPaths paths)
    {
        Create(paths);
        SetMeta(paths, "build_id", "01JBUILDID0000000000000000");
        SetMeta(paths, "build_utc", "2026-10-24T20:00:00.0000000+00:00");
        SetMeta(paths, "build_state", IndexSchema.CompleteState);
    }

    /// <summary>Inserts one indexed file and returns nothing; the id is one.</summary>
    /// <param name="paths">The index to write to.</param>
    /// <param name="state">The file's state, normally <c>ok</c>.</param>
    /// <param name="path">The workspace-relative path.</param>
    public static void InsertFile(IndexPaths paths, string state, string path = "src/Program.cs") =>
        Execute(
            paths,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"INSERT INTO files (path, language, size_bytes, mtime_utc, sha256, state, indexed_utc) VALUES ('{path}', 'csharp', 10, '2026-10-24T20:00:00.0000000+00:00', '{new string('a', 64)}', '{state}', '2026-10-24T20:00:00.0000000+00:00')"));

    /// <summary>Inserts one passage belonging to a file id that may not exist.</summary>
    /// <param name="paths">The index to write to.</param>
    /// <param name="fileId">The owning file id.</param>
    public static void InsertChunk(IndexPaths paths, long fileId) =>
        Execute(
            paths,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"INSERT INTO chunks (file_id, start_line, end_line, kind, text, tokens, sha256) VALUES ({fileId}, 1, 2, 'chunk', 'text', 'text', '{new string('b', 64)}')"));
}
