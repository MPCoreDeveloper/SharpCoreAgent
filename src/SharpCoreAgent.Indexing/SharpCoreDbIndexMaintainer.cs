using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Extraction;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The write side of the index: one build at a time, one build id per run, and never a throw because another
/// process got there first.
/// </summary>
/// <remarks>
/// The maintainer owns three things a reader does not: the writer lock, the build id and the clock. Every write
/// path in M1 enters through <see cref="EnsureFreshAsync"/>, which is what makes "a second process sees
/// <see cref="IndexFreshness.Building"/>" a property of the design instead of a race someone has to reason
/// about later.
/// <para>
/// The interface it grows into is <see cref="ICodeIndexMaintainer"/>: rebuild, verify and prune land with the
/// write path (T012, T014, T015), and shipping them as methods that quietly do nothing would be worse than not
/// having them yet.
/// </para>
/// </remarks>
internal sealed class SharpCoreDbIndexMaintainer : ICodeIndexMaintainer, IAsyncDisposable
{
    /// <summary>The line a caller prints when another process is mid-build.</summary>
    internal const string BuildingNote = "another SharpCoreAgent process is building this index; the last complete build is still readable";

    /// <summary>How long <see cref="RebuildAsync"/> waits for the writer lock before giving up.</summary>
    private static readonly TimeSpan RebuildLockBudget = TimeSpan.FromSeconds(30);

    /// <summary>The budget a rebuild runs under; a rebuild finishes the workspace or reports itself incomplete.</summary>
    private static readonly TimeSpan RebuildBudget = TimeSpan.FromHours(1);

    /// <summary>The most verification lines one result carries.</summary>
    private const int MaxDetails = 50;

    /// <summary>What a verification says when there is no index to verify.</summary>
    private const string MissingIndexNote = "no index for this workspace; run `sharpcoreagent index`";

    /// <summary>The statements that empty the tables a build fills, leaving the schema and the seed rows alone.</summary>
    /// <remarks>
    /// Each one carries <c>WHERE id &gt; 0</c> rather than being a bare <c>DELETE</c>: the engine's parser rejects a
    /// <c>DELETE</c> without a <c>WHERE</c>, and every row in these tables has a positive auto-increment id, so the
    /// predicate selects exactly everything.
    /// </remarks>
    private static readonly string[] ClearStatements =
    [
        "DELETE FROM node_embeddings WHERE id > 0",
        "DELETE FROM edges WHERE id > 0",
        "DELETE FROM chunks WHERE id > 0",
        "DELETE FROM symbols WHERE id > 0",
        "DELETE FROM docs WHERE id > 0",
        "DELETE FROM files WHERE id > 0",
    ];

    /// <summary>The tables whose rows name a file, and whose orphans a verification counts.</summary>
    private static readonly string[] OrphanQueries =
    [
        "SELECT file_id FROM chunks",
        "SELECT file_id FROM symbols",
        "SELECT file_id FROM docs",
    ];

    /// <summary>The read that re-hashes every stored passage against its own text.</summary>
    private const string PassageHashesQuery = "SELECT file_id, start_line, text, sha256 FROM chunks";

    private readonly Lock _gate = new();
    private readonly Workspace _workspace;
    private readonly IndexPaths _paths;
    private readonly SharpCoreDbCodeIndex _reader;
    private readonly string? _encryptionPassword;

    private IndexWriterLock? _writer;
    private string? _buildId;

    private SharpCoreDbIndexMaintainer(
        Workspace workspace,
        IndexPaths paths,
        SharpCoreDbCodeIndex reader,
        string? encryptionPassword)
    {
        _workspace = workspace;
        _paths = paths;
        _reader = reader;
        _encryptionPassword = encryptionPassword;
    }

    /// <summary>Gets the paths this maintainer writes.</summary>
    internal IndexPaths Paths => _paths;

    /// <summary>Gets the id of the run in progress, or of the last run this instance finished.</summary>
    internal string? BuildId
    {
        get
        {
            lock (_gate)
            {
                return _buildId;
            }
        }
    }

    /// <summary>Gets a value indicating whether this instance currently holds the writer lock.</summary>
    internal bool HoldsWriterLock
    {
        get
        {
            lock (_gate)
            {
                return _writer is not null;
            }
        }
    }

    /// <summary>
    /// Opens a maintainer for one workspace. Nothing is created or locked until the first build.
    /// </summary>
    /// <param name="workspace">The workspace whose index is written.</param>
    /// <param name="encryptionPassword">A password when the index is encrypted; otherwise <see langword="null"/>.</param>
    /// <returns>The maintainer, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    public static SharpCoreDbIndexMaintainer For(Workspace workspace, string? encryptionPassword = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var reader = SharpCoreDbCodeIndex.For(workspace, encryptionPassword);
        return new SharpCoreDbIndexMaintainer(workspace, IndexPaths.For(workspace), reader, encryptionPassword);
    }

    /// <summary>
    /// Brings the index up to date, waiting no longer than <paramref name="budget"/> for the writer lock.
    /// </summary>
    /// <param name="budget">The time the caller is willing to spend; the lock wait is part of it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// The report of the run, or a report whose status is <see cref="IndexFreshness.Building"/> when another
    /// writer kept the lock for the whole budget.
    /// </returns>
    public async ValueTask<IndexBuildReport> EnsureFreshAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        if (!await AcquireWriterAsync(budget, cancellationToken).ConfigureAwait(false))
        {
            return await BlockedAsync(started, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // The lock wait is part of the budget the caller gave, so a build that spent it queuing gets what is
            // left rather than a fresh allowance.
            var remaining = budget - Stopwatch.GetElapsedTime(started);
            return await BuildAsync(
                started,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWriter();
        }
    }

    /// <summary>
    /// Throws the rows away and builds the index again from the workspace as it is now.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The report of the new build.</returns>
    /// <remarks>
    /// The tables are emptied rather than the file deleted: another process may hold a handle on the database,
    /// and on Windows that handle is what makes a delete-and-recreate fail while an ordinary delete of every row
    /// succeeds. Everything a rebuild needs — the schema, the seed rows and the counters — survives.
    /// </remarks>
    public async ValueTask<IndexBuildReport> RebuildAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        if (!await AcquireWriterAsync(RebuildLockBudget, cancellationToken).ConfigureAwait(false))
        {
            return await BlockedAsync(started, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            ClearRows();
            return await BuildAsync(started, RebuildBudget, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWriter();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ReleaseWriter();
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> AcquireWriterAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_writer is not null)
            {
                // Re-entering from the same instance is not a second writer: the lock is already ours.
                return true;
            }
        }

        var writer = await IndexWriterLock.TryAcquireAsync(_paths, budget, cancellationToken);

        lock (_gate)
        {
            _writer = writer;
            return writer is not null;
        }
    }

    private void ReleaseWriter()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private async ValueTask<IndexBuildReport> BuildAsync(
        long started,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var buildId = IndexBuildId.NewId();

        _paths.EnsureDirectoryCreated();
        using var lease = IndexHandleCache.Acquire(_paths, _encryptionPassword, createIfMissing: true);
        var database = lease.Database.Database;
        IndexSchema.EnsureCreated(database, _paths);

        // Rows current with their file can still be stale with the code: a parser or analyzer contract this build
        // does not share means the stored passages were derived by rules it would not reproduce, and an
        // incremental pass would keep every one of them because the hashes still match.
        if (IndexSchema.RowsWereDerivedByAnotherContract(database))
        {
            await database.ExecuteBatchSQLAsync(ClearStatements, cancellationToken).ConfigureAwait(false);
        }

        var result = await IndexWriter
            .For(_workspace, database)
            .WriteAsync(budget, buildId, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _buildId = buildId;
        }

        _reader.InvalidateStatus();
        var status = _reader.GetStatus();

        return new IndexBuildReport
        {
            Status = status,
            Added = result.Added,
            Changed = result.Changed,
            Removed = result.Removed,
            Unchanged = result.Unchanged,
            Skipped = result.Skipped,
            ElapsedMilliseconds = Elapsed(started),
            Incomplete = result.Incomplete,
            Notes = result.Notes,
        };
    }

    /// <summary>
    /// Empties every table the build fills, leaving the schema and the seed rows in place.
    /// </summary>
    private void ClearRows()
    {
        using var lease = IndexHandleCache.Acquire(_paths, _encryptionPassword, createIfMissing: true);
        var database = lease.Database.Database;
        IndexSchema.EnsureCreated(database, _paths);

        database.ExecuteBatchSQL(ClearStatements);
        _reader.InvalidateStatus();
    }

    /// <summary>
    /// Reports that another writer kept the lock for the whole budget.
    /// </summary>
    /// <param name="started">The timestamp the call started at.</param>
    /// <param name="cancellationToken">Cancels the status read.</param>
    /// <returns>A report whose status is <see cref="IndexFreshness.Building"/>.</returns>
    private async ValueTask<IndexBuildReport> BlockedAsync(long started, CancellationToken cancellationToken)
    {
        var blocked = await _reader.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return new IndexBuildReport
        {
            Status = blocked with { Freshness = IndexFreshness.Building },
            Incomplete = true,
            ElapsedMilliseconds = Elapsed(started),
            Notes = [BuildingNote],
        };
    }

    /// <summary>
    /// Compares the index with the workspace and counts every way the two disagree.
    /// </summary>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>The verification; a missing index is unhealthy rather than an exception.</returns>
    /// <remarks>
    /// Four questions, each with its own repair: a file on disk that the index never saw, a row whose file is
    /// gone, a passage whose stored hash no longer matches its text, and a row that points at a file row nobody
    /// has. Reading every file is the expensive part, and it is the point of the command: <c>--verify</c> is what
    /// a caller runs when they no longer trust the incremental build's shortcuts.
    /// </remarks>
    public async ValueTask<IndexVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_paths.DatabaseExists)
        {
            return new IndexVerification { IsHealthy = false, Details = [MissingIndexNote] };
        }

        using var lease = IndexHandleCache.Acquire(_paths, _encryptionPassword, createIfMissing: false);
        var database = lease.Database.Database;
        var stored = IndexStoredFile.LoadAll(database);
        var scanned = WorkspaceFileScanner.For(_workspace).Scan(cancellationToken);

        var details = new List<string>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var missingFromIndex = 0;

        foreach (var file in scanned)
        {
            present.Add(file.Path);

            if (!file.IsIndexable || stored.ContainsKey(file.Path))
            {
                continue;
            }

            missingFromIndex++;
            AddDetail(details, string.Create(CultureInfo.InvariantCulture, $"not indexed: {file.Path}"));
        }

        var missingOnDisk = 0;
        foreach (var row in Ordered(stored).Where(row => !present.Contains(row.Path)))
        {
            missingOnDisk++;
            AddDetail(details, string.Create(CultureInfo.InvariantCulture, $"gone: {row.Path}"));
        }

        var hashMismatches = await CountFileMismatchesAsync(stored, details, cancellationToken).ConfigureAwait(false);
        hashMismatches += CountPassageMismatches(database, details);
        var orphans = CountOrphans(database, stored, details);

        return new IndexVerification
        {
            IsHealthy = missingFromIndex == 0 && missingOnDisk == 0 && hashMismatches == 0 && orphans == 0,
            FilesMissingFromIndex = missingFromIndex,
            FilesMissingOnDisk = missingOnDisk,
            HashMismatches = hashMismatches,
            Orphans = orphans,
            Details = details,
        };
    }

    /// <summary>
    /// Drops the rows of files that no longer exist, without touching anything else.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A report whose <see cref="IndexBuildReport.Removed"/> counts the dropped files.</returns>
    public async ValueTask<IndexBuildReport> PruneAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        if (!await AcquireWriterAsync(RebuildLockBudget, cancellationToken).ConfigureAwait(false))
        {
            return await BlockedAsync(started, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var notes = new List<string>();
            var removed = 0;

            if (_paths.DatabaseExists)
            {
                using var lease = IndexHandleCache.Acquire(_paths, _encryptionPassword, createIfMissing: false);
                var database = lease.Database.Database;
                var stored = IndexStoredFile.LoadAll(database);
                var present = new HashSet<string>(StringComparer.Ordinal);

                foreach (var file in WorkspaceFileScanner.For(_workspace).Scan(cancellationToken))
                {
                    present.Add(file.Path);
                }

                var writer = IndexFileWriter.For(database);
                foreach (var row in Ordered(stored).Where(row => !present.Contains(row.Path)))
                {
                    writer.Delete(row.Id);
                    removed++;
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"removed {row.Path}"));
                }

                RefreshCounters(database);
                _reader.InvalidateStatus();
            }

            return new IndexBuildReport
            {
                Status = _reader.GetStatus(),
                Removed = removed,
                ElapsedMilliseconds = Elapsed(started),
                Notes = notes,
            };
        }
        finally
        {
            ReleaseWriter();
        }
    }

    /// <summary>
    /// Counts the files whose content no longer matches the hash the index stored.
    /// </summary>
    /// <param name="stored">The stored rows, by path.</param>
    /// <param name="details">Where one line per disagreement is written.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many files changed.</returns>
    private async ValueTask<int> CountFileMismatchesAsync(
        IReadOnlyDictionary<string, IndexStoredFile> stored,
        List<string> details,
        CancellationToken cancellationToken)
    {
        var mismatch = 0;

        foreach (var row in Ordered(stored).Where(row => string.Equals(row.State, IndexFileStates.Ok, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_workspace.TryResolve(row.Path, out var fullPath, out _) || !File.Exists(fullPath))
            {
                // "Gone" is counted once, above; a missing file is not also a hash mismatch.
                continue;
            }

            try
            {
                var content = await IndexFileContent.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (string.Equals(content.Sha256, row.Sha256, StringComparison.Ordinal))
                {
                    continue;
                }

                mismatch++;
                AddDetail(details, string.Create(CultureInfo.InvariantCulture, $"changed on disk: {row.Path}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                mismatch++;
                AddDetail(details, string.Create(
                    CultureInfo.InvariantCulture,
                    $"unreadable: {row.Path} ({ex.Message})"));
            }
        }

        return mismatch;
    }

    /// <summary>
    /// Counts the passages whose stored hash no longer matches their stored text.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <param name="details">Where one line per disagreement is written.</param>
    /// <returns>How many passages were tampered with or written by an older contract.</returns>
    private static int CountPassageMismatches(IDatabase database, List<string> details)
    {
        var mismatch = 0;

        foreach (var row in IndexSql.Query(database, PassageHashesQuery))
        {
            var text = IndexSql.AsText(row.GetValueOrDefault("text"));
            var storedHash = IndexSql.AsText(row.GetValueOrDefault("sha256"));

            if (string.Equals(IndexFileContent.HashText(text), storedHash, StringComparison.Ordinal))
            {
                continue;
            }

            mismatch++;
            AddDetail(details, string.Create(
                CultureInfo.InvariantCulture,
                $"passage {IndexSql.AsLong(row.GetValueOrDefault("file_id"))}:{IndexSql.AsLong(row.GetValueOrDefault("start_line"))} does not match its hash"));
        }

        return mismatch;
    }

    /// <summary>
    /// Counts the rows that point at a file row nobody has.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <param name="stored">The stored file rows.</param>
    /// <param name="details">Where one line per orphan is written.</param>
    /// <returns>How many orphan rows the index holds.</returns>
    private static int CountOrphans(
        IDatabase database,
        IReadOnlyDictionary<string, IndexStoredFile> stored,
        List<string> details)
    {
        var known = new HashSet<long>(stored.Values.Select(static row => row.Id));
        var orphans = 0;

        foreach (var sql in OrphanQueries)
        {
            foreach (var row in IndexSql.Query(database, sql))
            {
                var fileId = IndexSql.AsLong(row.GetValueOrDefault("file_id"));
                if (known.Contains(fileId))
                {
                    continue;
                }

                orphans++;
                AddDetail(details, string.Create(CultureInfo.InvariantCulture, $"orphan row for file {fileId}"));
            }
        }

        return orphans;
    }

    /// <summary>
    /// Rewrites the <c>meta</c> counters from the tables as they are now.
    /// </summary>
    /// <param name="database">An open index database.</param>
    private static void RefreshCounters(IDatabase database)
    {
        IndexFileWriter.For(database).WriteMeta(
        [
            ("file_count", IndexSql.Count(database, IndexTable.Files).ToString(CultureInfo.InvariantCulture)),
            ("chunk_count", IndexSql.Count(database, IndexTable.Chunks).ToString(CultureInfo.InvariantCulture)),
            ("symbol_count", IndexSql.Count(database, IndexTable.Symbols).ToString(CultureInfo.InvariantCulture)),
            ("edge_count", IndexSql.Count(database, IndexTable.Edges).ToString(CultureInfo.InvariantCulture)),
        ]);
    }

    /// <summary>
    /// Returns the stored rows in a stable order, so a verification prints the same lines twice.
    /// </summary>
    /// <param name="stored">The rows, by path.</param>
    /// <returns>The rows ordered by path.</returns>
    private static IEnumerable<IndexStoredFile> Ordered(IReadOnlyDictionary<string, IndexStoredFile> stored) =>
        stored.Values.OrderBy(static row => row.Path, StringComparer.Ordinal);

    /// <summary>
    /// Adds one verification line, stopping once the list is long enough to be a wall of text.
    /// </summary>
    /// <param name="details">The list so far.</param>
    /// <param name="line">The line to add.</param>
    private static void AddDetail(List<string> details, string line)
    {
        if (details.Count < MaxDetails)
        {
            details.Add(line);
            return;
        }

        if (details.Count == MaxDetails)
        {
            details.Add(string.Create(CultureInfo.InvariantCulture, $"... more than {MaxDetails} disagreements"));
        }
    }

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
