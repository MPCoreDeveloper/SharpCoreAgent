using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Indexing.Extraction;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// What one build did, in the counters <see cref="SharpCoreAgent.Domain.Index.IndexBuildReport"/> reports.
/// </summary>
/// <remarks>
/// The counters are disjoint and cover every file the scanner returned: added, changed, removed, unchanged or
/// skipped. That is what makes "0 changed" an assertion a test can make rather than a number to interpret.
/// </remarks>
internal sealed record IndexWriteResult
{
    /// <summary>Gets how many files were indexed for the first time.</summary>
    public required int Added { get; init; }

    /// <summary>Gets how many files were rewritten because their content changed.</summary>
    public required int Changed { get; init; }

    /// <summary>Gets how many files were dropped because they no longer exist.</summary>
    public required int Removed { get; init; }

    /// <summary>Gets how many files were looked at and left alone.</summary>
    public required int Unchanged { get; init; }

    /// <summary>Gets how many files were skipped by policy or could not be read.</summary>
    public required int Skipped { get; init; }

    /// <summary>Gets a value indicating whether the budget stopped the build early.</summary>
    public required bool Incomplete { get; init; }

    /// <summary>Gets one line per decision worth explaining.</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// One incremental build: walk the workspace, write what changed, drop what vanished and record the run.
/// </summary>
/// <remarks>
/// A writer instance belongs to exactly one build. The counters and the note list are its state, and the
/// maintainer creates a new one per call rather than resetting fields between runs, because a build that threw
/// halfway must not leave anything behind that the next one reads.
/// <para>
/// Two passes, both bounded by the same budget. The first writes each changed file's rows and remembers its
/// relations; the second resolves those relations into edges once every declaration of the build is stored.
/// Splitting them is what makes an edge from file 1 to a symbol declared in file 5 possible at all.
/// </para>
/// </remarks>
internal sealed class IndexWriter
{
    /// <summary>The most skip notes one report carries, so a repository full of images does not print itself.</summary>
    private const int MaxSkipNotes = 20;

    private readonly Workspace _workspace;
    private readonly IDatabase _database;
    private readonly IndexFileWriter _files;

    private readonly List<string> _notes = [];
    private readonly List<WrittenFile> _written = [];

    private int _added;
    private int _changed;
    private int _removed;
    private int _unchanged;
    private int _skipped;
    private bool _incomplete;

    private IndexWriter(Workspace workspace, IDatabase database, IndexFileWriter files)
    {
        _workspace = workspace;
        _database = database;
        _files = files;
    }

    /// <summary>
    /// Creates a writer for one open index database.
    /// </summary>
    /// <param name="workspace">The workspace to walk.</param>
    /// <param name="database">The database to write to.</param>
    /// <returns>The writer, which is used for one build and then discarded.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IndexWriter For(Workspace workspace, IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(database);

        return new IndexWriter(workspace, database, IndexFileWriter.For(database));
    }

    /// <summary>
    /// Brings the index up to date, stopping when the budget expires.
    /// </summary>
    /// <param name="budget">The wall-clock time the walk and the writes may take.</param>
    /// <param name="buildId">The id written to <c>meta['build_id']</c> when the build finishes.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The counters and notes of the run.</returns>
    public async ValueTask<IndexWriteResult> WriteAsync(TimeSpan budget, string buildId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);

        var started = Stopwatch.GetTimestamp();
        var scanned = WorkspaceFileScanner.For(_workspace).Scan(cancellationToken);
        var stored = IndexStoredFile.LoadAll(_database);
        var indexedUtc = IndexTimestamps.Format(DateTimeOffset.UtcNow);

        foreach (var file in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Stopwatch.GetElapsedTime(started) >= budget)
            {
                _incomplete = true;
                _notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"budget reached after {_added + _changed + _unchanged + _skipped} of {scanned.Count} files"));
                break;
            }

            await IndexOneAsync(file, stored, indexedUtc, cancellationToken).ConfigureAwait(false);
        }

        RemoveVanished(scanned, stored);
        WriteEdges();
        WriteMeta(buildId, IndexTimestamps.Format(DateTimeOffset.UtcNow));

        return new IndexWriteResult
        {
            Added = _added,
            Changed = _changed,
            Removed = _removed,
            Unchanged = _unchanged,
            Skipped = _skipped,
            Incomplete = _incomplete,
            Notes = _notes,
        };
    }

    /// <summary>
    /// Decides what one scanned file needs and writes it.
    /// </summary>
    /// <param name="file">The scanned file.</param>
    /// <param name="stored">The rows already in the index, by path.</param>
    /// <param name="indexedUtc">The timestamp to record.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    private async ValueTask IndexOneAsync(
        ScannedFile file,
        Dictionary<string, IndexStoredFile> stored,
        string indexedUtc,
        CancellationToken cancellationToken)
    {
        stored.TryGetValue(file.Path, out var previous);

        if (!file.IsIndexable)
        {
            RecordSkipped(file, previous, IndexFileStates.Skipped, file.SkipReason ?? "skipped by policy", indexedUtc);
            return;
        }

        IndexFileContent content;
        try
        {
            content = await IndexFileContent.ReadAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is recorded, not fatal: one locked file must not cost the build the
            // other four hundred. It is stored as an error so the next refresh tries it again.
            RecordSkipped(file, previous, IndexFileStates.Error, "unreadable: " + ex.Message, indexedUtc);
            return;
        }

        if (previous is { State: IndexFileStates.Ok } && previous.Matches(file, content.Sha256))
        {
            _unchanged++;
            return;
        }

        _written.Add(_files.Write(file, content, previous?.Id, indexedUtc, cancellationToken));

        if (previous is null)
        {
            _added++;
            return;
        }

        _changed++;
    }

    /// <summary>
    /// Records a file that has no rows of its own.
    /// </summary>
    /// <param name="file">The scanned file.</param>
    /// <param name="previous">Its stored row, or <see langword="null"/>.</param>
    /// <param name="state"><see cref="IndexFileStates.Skipped"/> or <see cref="IndexFileStates.Error"/>.</param>
    /// <param name="reason">Why the file holds nothing.</param>
    /// <param name="indexedUtc">The timestamp to record.</param>
    private void RecordSkipped(
        ScannedFile file,
        IndexStoredFile? previous,
        string state,
        string reason,
        string indexedUtc)
    {
        var unchanged = previous is not null
            && string.Equals(previous.State, state, StringComparison.Ordinal)
            && string.Equals(previous.SkipReason, reason, StringComparison.Ordinal)
            && string.Equals(previous.MtimeUtc, IndexTimestamps.Format(file.ModifiedUtc), StringComparison.Ordinal)
            && previous.SizeBytes == file.SizeBytes;

        if (unchanged)
        {
            _unchanged++;
            return;
        }

        _files.WritePlaceholder(file, previous?.Id, state, reason, indexedUtc);
        _skipped++;

        if (_notes.Count < MaxSkipNotes)
        {
            _notes.Add(string.Create(CultureInfo.InvariantCulture, $"{file.Path}: {reason}"));
        }
    }

    /// <summary>
    /// Drops the rows of files the scanner no longer sees.
    /// </summary>
    /// <param name="scanned">Everything the walk found.</param>
    /// <param name="stored">Everything the index held before the build.</param>
    private void RemoveVanished(IReadOnlyList<ScannedFile> scanned, Dictionary<string, IndexStoredFile> stored)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in scanned)
        {
            present.Add(file.Path);
        }

        var vanished = stored.Values
            .Where(row => !present.Contains(row.Path))
            .OrderBy(static row => row.Path, StringComparer.Ordinal)
            .ToList();

        foreach (var row in vanished)
        {
            _files.Delete(row.Id);
            _removed++;
            _notes.Add(string.Create(CultureInfo.InvariantCulture, $"removed {row.Path}"));
        }
    }

    /// <summary>
    /// Resolves the relations of the files this build wrote into edges.
    /// </summary>
    /// <remarks>
    /// A relation whose source or target this build cannot name is dropped rather than stored as a dangling row.
    /// The cost of that choice is one rebuild for a target that appears later; the cost of the other is a graph
    /// that answers with rows pointing at nothing.
    /// </remarks>
    private void WriteEdges()
    {
        if (_written.Count == 0)
        {
            return;
        }

        var resolver = IndexEdgeResolver.Load(_database);
        var seen = new HashSet<(long Source, long Target, string Relation)>();
        var edges = new List<IndexEdge>();

        foreach (var file in _written.OrderBy(static written => written.Path, StringComparer.Ordinal))
        {
            foreach (var relation in file.Relations)
            {
                if (relation.Source is null
                    || !resolver.TryResolveKey(relation.Source, out var source)
                    || !resolver.TryResolve(relation.Target, out var target)
                    || source == target
                    || !seen.Add((source, target, relation.Kind)))
                {
                    continue;
                }

                edges.Add(new IndexEdge(source, target, relation.Kind, relation.Weight, file.FileId));
            }
        }

        _files.WriteEdges(edges);
    }

    /// <summary>
    /// Records the run: its id, its timestamp, whether it finished and what the index now holds.
    /// </summary>
    /// <param name="buildId">The id of this build.</param>
    /// <param name="buildUtc">When the build finished.</param>
    /// <remarks>
    /// Written last, in one batch, so an interrupted build leaves the previous id in place and the reader can
    /// tell that the rows it is looking at were never completed.
    /// </remarks>
    private void WriteMeta(string buildId, string buildUtc)
    {
        _files.WriteMeta(
        [
            ("build_id", buildId),
            ("build_utc", buildUtc),
            ("build_state", _incomplete ? IndexSchema.PartialState : IndexSchema.CompleteState),
            ("file_count", Counter(IndexTable.Files)),
            ("chunk_count", Counter(IndexTable.Chunks)),
            ("symbol_count", Counter(IndexTable.Symbols)),
            ("edge_count", Counter(IndexTable.Edges)),
        ]);
    }

    /// <summary>
    /// Reads one table's row count in the format <c>meta</c> stores.
    /// </summary>
    /// <param name="table">The table to count.</param>
    /// <returns>The count as text.</returns>
    private string Counter(IndexTable table) =>
        IndexSql.Count(_database, table).ToString(CultureInfo.InvariantCulture);
}
