using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Extraction;
using SharpCoreAgent.Indexing.Graph;
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

    /// <summary>Gets what each step of the build cost, in the order it ran them.</summary>
    public required IReadOnlyList<IndexPhase> Phases { get; init; }

    /// <summary>Gets how many times the build asked the store to commit.</summary>
    /// <remarks>
    /// Counted by the writer where it times the commit, which is what makes the round's arithmetic — 700 batches for a
    /// rebuild become 24 — a number a test asserts instead of one a reader infers from seconds (spec 026, D7).
    /// </remarks>
    public required int Commits { get; init; }
}

/// <summary>
/// One incremental build: walk the workspace, write what changed, drop what vanished and record the run.
/// </summary>
/// <remarks>
/// A writer instance belongs to exactly one build. The counters and the note list are its state, and the
/// maintainer creates a new one per call rather than resetting fields between runs, because a build that threw
/// halfway must not leave anything behind that the next one reads.
/// <para>
/// Two passes, both bounded by the same budget, measured on the clock the caller named. The first writes each
/// changed file's rows and remembers its relations; the second resolves those relations into edges once every
/// declaration of the build is stored. Splitting them is what makes an edge from file 1 to a symbol declared in
/// file 5 possible at all.
/// </para>
/// <para>
/// The first pass commits once per checkpoint of <see cref="CheckpointFiles"/> files rather than once per file, because
/// the store charges for the commit and not for the rows (spec 002, D21; spec 026). Everything else is unchanged: one
/// file is still written as one unit, <c>meta</c> is still last, and a build that stops early keeps what it wrote.
/// </para>
/// </remarks>
internal sealed class IndexWriter
{
    /// <summary>The most skip notes one report carries, so a repository full of images does not print itself.</summary>
    private const int MaxSkipNotes = 20;

    /// <summary>The number of files a checkpoint carries before the store is asked to commit.</summary>
    /// <remarks>
    /// Arithmetic, revised by measurement: spec 002 D21 priced a commit at 1.36 s against about 1.5 s for a file, and after
    /// this round the file itself costs 49 ms while a commit still costs 1.44 s — so at 32 files the commit is about half
    /// the write phase, and a larger group would help further. What bounds it is the batch's buffer rather than the
    /// re-work (32 files is 1.6 s to redo), and the size of that buffer is a decision this round did not take (spec 026,
    /// D3). A smaller group buys little: 4 files a checkpoint still pays 189 commits for this rebuild.
    /// </remarks>
    internal const int CheckpointFiles = 32;

    private readonly Workspace _workspace;
    private readonly IDatabase _database;
    private readonly IndexFileWriter _files;
    private readonly IndexPhaseClock _phases;
    private readonly int _checkpointFiles;

    private readonly List<string> _notes = [];
    private readonly List<WrittenFile> _written = [];

    private int _added;
    private int _changed;
    private int _removed;
    private int _unchanged;
    private int _skipped;
    private bool _incomplete;

    private IndexWriter(
        Workspace workspace,
        IDatabase database,
        IndexFileWriter files,
        IndexPhaseClock phases,
        int checkpointFiles)
    {
        _workspace = workspace;
        _database = database;
        _files = files;
        _phases = phases;
        _checkpointFiles = checkpointFiles;
    }

    /// <summary>
    /// Creates a writer for one open index database.
    /// </summary>
    /// <param name="workspace">The workspace to walk.</param>
    /// <param name="database">The database to write to.</param>
    /// <param name="checkpointFiles">
    /// How many files one commit carries, or <see cref="CheckpointFiles"/> for the build's own answer. A caller that is
    /// not a build has no reason to pass one; a test does, because the arithmetic of a checkpoint is worth asserting
    /// with two files rather than thirty-two (spec 026, D3).
    /// </param>
    /// <returns>The writer, which is used for one build and then discarded.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="checkpointFiles"/> is not positive.</exception>
    public static IndexWriter For(Workspace workspace, IDatabase database, int checkpointFiles = CheckpointFiles)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointFiles);

        // One clock for the writer and the file writer: the store's commit is timed inside the file writer and has to
        // land in the same report as the steps that contain it.
        var phases = new IndexPhaseClock();

        return new IndexWriter(
            workspace,
            database,
            IndexFileWriter.For(database, phases: phases),
            phases,
            checkpointFiles);
    }

    /// <summary>
    /// Brings the index up to date, stopping when the budget expires.
    /// </summary>
    /// <param name="budget">The time the walk and the writes may take, measured on <paramref name="clock"/>.</param>
    /// <param name="clock">
    /// Which clock <paramref name="budget"/> is measured on. Wall clock is what a caller who is waiting measures;
    /// processor time is what a caller who asked for work measures, and it is the one that does not move while the
    /// machine is suspended.
    /// </param>
    /// <param name="buildId">The id written to <c>meta['build_id']</c> when the build finishes.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The counters and notes of the run.</returns>
    public async ValueTask<IndexWriteResult> WriteAsync(
        TimeSpan budget,
        IndexBudgetClock clock,
        string buildId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);

        var started = Stopwatch.GetTimestamp();
        var processorStarted = Environment.CpuUsage;

        var scanned = _phases.Measure(
            IndexPhases.Scan,
            () => WorkspaceFileScanner.For(_workspace).Scan(cancellationToken));

        var stored = _phases.Measure(IndexPhases.Load, () => IndexStoredFile.LoadAll(_database));
        var indexedUtc = IndexTimestamps.Format(DateTimeOffset.UtcNow);

        // Process-wide on purpose: a build is one process doing one thing, and the number a developer reads for
        // "how much work did this cost" is the process's, not one thread's.
        bool Spent() => clock == IndexBudgetClock.ProcessorTime
            ? Environment.CpuUsage.TotalTime - processorStarted.TotalTime >= budget
            : Stopwatch.GetElapsedTime(started) >= budget;

        await _phases.MeasureAsync(IndexPhases.Write, async () =>
        {
            // One owner of the batch: the loop opens the checkpoint and the loop closes it. The first write inside the
            // checkpoint is what opens the store's batch, so a build that stops before its first file pays nothing for
            // the checkpoint at all (spec 026, D2).
            _files.BeginCheckpoint();

            try
            {
                foreach (var file in scanned)
                {
                    // Both stops commit first. What the open checkpoint holds is work this run has already paid for,
                    // and a build that reported it without keeping it would make its own counters a lie (D5).
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _files.EndCheckpoint();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (Spent())
                    {
                        _incomplete = true;
                        _notes.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"budget reached after {_added + _changed + _unchanged + _skipped} of {scanned.Count} files"));
                        break;
                    }

                    await IndexOneAsync(file, stored, indexedUtc, cancellationToken).ConfigureAwait(false);

                    if (_files.FilesInCheckpoint >= _checkpointFiles)
                    {
                        _files.EndCheckpoint();
                        _files.BeginCheckpoint();
                    }
                }

                // Reached by the end of the walk and by the budget's break; not by cancellation, which left above.
                _files.EndCheckpoint();
            }
            catch (Exception)
            {
                // Nothing of an abandoned checkpoint reaches the store, and the build fails exactly as it did when a
                // file was the unit: the report is lost, and the `meta` row nobody wrote is what leaves the index stale.
                _files.AbandonCheckpoint();
                throw;
            }
        }).ConfigureAwait(false);

        _phases.Measure(IndexPhases.Remove, () => RemoveVanished(scanned, stored));
        _phases.Measure(IndexPhases.Edges, WriteEdges);

        // Communities are a property of the whole graph, so they are recomputed exactly when the graph could have
        // changed: a build that wrote nothing and removed nothing would pay for Louvain to reach the same answer.
        var assigned = CommunityAssignment.None;
        if (_written.Count > 0 || _removed > 0)
        {
            assigned = await _phases.MeasureAsync(
                IndexPhases.Communities,
                () => CommunityAssigner.AssignAsync(_database, _phases, cancellationToken)).ConfigureAwait(false);
        }

        _phases.Measure(IndexPhases.Meta, () => WriteMeta(buildId, IndexTimestamps.Format(DateTimeOffset.UtcNow)));

        return new IndexWriteResult
        {
            Added = _added,
            Changed = _changed,
            Removed = _removed,
            Unchanged = _unchanged,
            Skipped = _skipped,
            Incomplete = _incomplete,
            Notes = _notes,
            Phases = _phases.Elapsed(),

            // The clusterer writes its own columns in a batch of its own rather than through the file writer, so its
            // commit is added here: `Commits` is every commit the build asked for, not the file writer's share of them.
            Commits = _files.Commits + assigned.Commits,
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

        if (vanished.Count == 0)
        {
            return;
        }

        // One commit for the set, which is the same lever as the write loop and the same failure unit: a build that dies
        // pruning leaves the rows it already dropped committed and the rest as they were (spec 026, D6).
        _files.BeginCheckpoint();

        try
        {
            foreach (var row in vanished)
            {
                _files.Delete(row.Id);
                _removed++;
                _notes.Add(string.Create(CultureInfo.InvariantCulture, $"removed {row.Path}"));
            }

            _files.EndCheckpoint();
        }
        catch (Exception)
        {
            _files.AbandonCheckpoint();
            throw;
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
    /// <para>
    /// The schema, parser and analyzer contract keys are rewritten here for the same reason the counters are:
    /// they describe the rows this run left behind, and a build that re-derived them under a newer contract
    /// would otherwise be reported stale for ever against the values the first open seeded.
    /// </para>
    /// <para>
    /// A pass that wrote no row and removed no file has left every table exactly as the last completed build left
    /// it, so it records the counters that build already recorded instead of reading them again: counting
    /// <c>chunks</c> is a scan of every passage — 2 598 ms of 14 434 rows on this repository's index — spent to
    /// confirm a number the run did not change.
    /// </para>
    /// <para>
    /// And when every key but the id and timestamp would be rewritten with the value it already holds, the batch is
    /// not committed at all. A batch over <c>meta</c> is not free: SharpCoreDB reads the pages a batch touches, so
    /// committing ten rows against a 73 MiB index measured 1 774 ms after a run that had already read those pages and
    /// 5 107 ms after one that had not — the price of the scans this method used to run did not leave the run, it moved
    /// into the commit (spec 002, D27). Skipping the write is what removes the cost: nothing changed, so nothing is
    /// recorded, and <c>meta['build_utc']</c> keeps naming the build that did change something.
    /// </para>
    /// <para>
    /// The one state this cannot see is a build that died between its rows and this write: its rows are stored while
    /// the counters and id still describe the build before it, so the next pass that writes anything recounts all four
    /// tables and stamps a new id.
    /// </para>
    /// </remarks>
    private void WriteMeta(string buildId, string buildUtc)
    {
        var stored = _written.Count == 0 && _removed == 0 ? IndexSchema.ReadMeta(_database) : null;

        (string Key, string Value)[] rows =
        [
            ("schema_version", IndexSchema.Version.ToString(CultureInfo.InvariantCulture)),
            ("parser_version", IndexSchema.ParserVersion.ToString(CultureInfo.InvariantCulture)),
            ("analyzer_contract", IndexSchema.AnalyzerContract.ToString(CultureInfo.InvariantCulture)),
            ("build_id", buildId),
            ("build_utc", buildUtc),
            ("build_state", _incomplete ? IndexSchema.PartialState : IndexSchema.CompleteState),
            ("file_count", Counter(IndexTable.Files, stored)),
            ("chunk_count", Counter(IndexTable.Chunks, stored)),
            ("symbol_count", Counter(IndexTable.Symbols, stored)),
            ("edge_count", Counter(IndexTable.Edges, stored)),
        ];

        if (stored is not null && Describes(rows, stored))
        {
            return;
        }

        _files.WriteMeta(rows);
    }

    /// <summary>
    /// Whether the run's keys already hold the values the index stores, its own id and timestamp aside.
    /// </summary>
    /// <param name="rows">The keys this run would write.</param>
    /// <param name="stored">The keys the index holds.</param>
    /// <returns>
    /// <see langword="true"/> when every key but <c>build_id</c> and <c>build_utc</c> would be rewritten unchanged, so
    /// the write could not tell a reader anything the store does not already say.
    /// </returns>
    private static bool Describes(
        IEnumerable<(string Key, string Value)> rows,
        Dictionary<string, string> stored) =>
        rows.All(row => row.Key is "build_id" or "build_utc"
            || (stored.TryGetValue(row.Key, out var known) && string.Equals(known, row.Value, StringComparison.Ordinal)));

    /// <summary>
    /// Reads one table's row count in the format <c>meta</c> stores.
    /// </summary>
    /// <param name="table">The table to count.</param>
    /// <param name="stored">
    /// The counters as the last completed build wrote them, when this pass changed no table, or
    /// <see langword="null"/> when it did.
    /// </param>
    /// <returns>The count as text.</returns>
    private string Counter(IndexTable table, Dictionary<string, string>? stored) =>
        stored is not null && stored.TryGetValue(MetaKey(table), out var known)
            ? known
            : IndexSql.Count(_database, table).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the <c>meta</c> key that holds one table's row count.
    /// </summary>
    /// <param name="table">One of the four tables <c>meta</c> counts.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The table has no counter in <c>meta</c>.</exception>
    private static string MetaKey(IndexTable table) => table switch
    {
        IndexTable.Files => "file_count",
        IndexTable.Chunks => "chunk_count",
        IndexTable.Symbols => "symbol_count",
        IndexTable.Edges => "edge_count",
        _ => throw new ArgumentOutOfRangeException(
            nameof(table),
            table,
            "Only files, chunks, symbols and edges are counted in meta."),
    };
}
