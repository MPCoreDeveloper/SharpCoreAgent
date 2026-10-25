using SharpCoreAgent.Indexing.Extraction;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// One row of <c>files</c>, as the build compares against what the scanner just found.
/// </summary>
/// <remarks>
/// The comparison is the whole point of the record: size, timestamp and content hash are the three facts that
/// decide whether a file needs extracting again, and keeping them together stops the decision from being
/// re-derived differently in the build, the verification and the prune.
/// </remarks>
internal sealed record IndexStoredFile
{
    /// <summary>Gets the row id every child row points at.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the workspace-relative path, with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the size on disk when the row was written.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Gets the last-write timestamp when the row was written, in the stored format.</summary>
    public required string MtimeUtc { get; init; }

    /// <summary>Gets the content hash when the row was written.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the file's state: <c>ok</c>, <c>skipped</c>, <c>stale</c> or <c>error</c>.</summary>
    public required string State { get; init; }

    /// <summary>Gets the reason a skipped or failed file carries, or <see langword="null"/> for an indexed one.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Gets a value indicating whether the stored row describes the file the scanner just saw.</summary>
    /// <param name="file">The scanned file.</param>
    /// <param name="sha256">The hash of the file's current bytes.</param>
    /// <returns><see langword="true"/> when size, timestamp and hash all agree.</returns>
    public bool Matches(ScannedFile file, string sha256)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(sha256);

        return SizeBytes == file.SizeBytes
            && string.Equals(MtimeUtc, IndexTimestamps.Format(file.ModifiedUtc), StringComparison.Ordinal)
            && string.Equals(Sha256, sha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads every file row.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>The rows, keyed by path with ordinal comparison.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public static Dictionary<string, IndexStoredFile> LoadAll(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var rows = new Dictionary<string, IndexStoredFile>(StringComparer.Ordinal);
        var result = IndexSql.Query(
            database,
            "SELECT id, path, size_bytes, mtime_utc, sha256, state, skip_reason FROM files");

        foreach (var row in result)
        {
            var path = IndexSql.AsText(row.GetValueOrDefault("path"));
            if (path.Length == 0)
            {
                continue;
            }

            rows[path] = new IndexStoredFile
            {
                Id = IndexSql.AsLong(row.GetValueOrDefault("id")),
                Path = path,
                SizeBytes = IndexSql.AsLong(row.GetValueOrDefault("size_bytes")),
                MtimeUtc = IndexSql.AsText(row.GetValueOrDefault("mtime_utc")),
                Sha256 = IndexSql.AsText(row.GetValueOrDefault("sha256")),
                State = IndexSql.AsText(row.GetValueOrDefault("state")),
                SkipReason = IndexSql.AsOptionalText(row.GetValueOrDefault("skip_reason")),
            };
        }

        return rows;
    }
}

/// <summary>
/// The values <c>files.state</c> is allowed to hold.
/// </summary>
/// <remarks>
/// The names match the schema's <c>CHECK</c> constraint exactly. <c>Error</c> is a file that was indexable but
/// could not be read this run: it counts as stale on purpose, so the next refresh tries it again instead of
/// treating a transient lock as a permanent decision.
/// </remarks>
internal static class IndexFileStates
{
    /// <summary>The file was extracted and its rows are current.</summary>
    public const string Ok = "ok";

    /// <summary>The file was deliberately not extracted, for example because it is binary.</summary>
    public const string Skipped = "skipped";

    /// <summary>The file changed since the row was written.</summary>
    public const string Stale = "stale";

    /// <summary>The file could not be read when the build reached it.</summary>
    public const string Error = "error";
}
