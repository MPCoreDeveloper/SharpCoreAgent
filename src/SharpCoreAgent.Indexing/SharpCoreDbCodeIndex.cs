using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The reader's view of one workspace index: what is stored, how healthy it is, and one shared handle.
/// </summary>
/// <remarks>
/// Three rules shape this type.
/// <list type="number">
/// <item>Absence is a value. A missing, half-written, foreign or future index comes back as an
/// <see cref="IndexStatus"/>; the only methods that throw are the ones a caller calls by mistake.</item>
/// <item>The status is computed from <c>meta</c> and the <c>files</c> table, and cached against the database
/// file's length and write time, so a status read after a rebuild in another process is corrected without a
/// cache flush.</item>
/// <item>The second open in this process reuses the first handle through <see cref="IndexHandleCache"/>: the
/// engine keeps a lock on its file, and a builder and a reader that disagree about what is committed would be
/// worse than a lock error.</item>
/// </list>
/// The retrieval methods of <see cref="ICodeIndex"/> arrive with the legs that answer them; this type grows
/// into that interface in M2 instead of shipping stubs that would silently return nothing.
/// </remarks>
internal sealed class SharpCoreDbCodeIndex : IDisposable
{
    private static readonly string[] OrphanedByFileTables = ["chunks", "symbols", "docs"];

    private readonly Lock _gate = new();
    private readonly IndexPaths _paths;
    private readonly string? _encryptionPassword;

    private IndexHandleLease? _lease;
    private IndexStatus? _cached;
    private FileStamp? _cachedStamp;

    private SharpCoreDbCodeIndex(IndexPaths paths, string? encryptionPassword)
    {
        _paths = paths;
        _encryptionPassword = encryptionPassword;
    }

    /// <summary>Gets the paths this reader reads.</summary>
    internal IndexPaths Paths => _paths;

    /// <summary>
    /// Gets the last reason the index was called unreadable, stale or building, or <see langword="null"/> when
    /// it is fresh.
    /// </summary>
    internal string? LastReason { get; private set; }

    /// <summary>
    /// Gets the cached open handle, or <see langword="null"/> before the first read of an existing index.
    /// </summary>
    internal IDatabase? Database
    {
        get
        {
            lock (_gate)
            {
                return _lease?.Database.Database;
            }
        }
    }

    /// <summary>
    /// Opens a reader for one workspace. Nothing is created and nothing is read until the first call.
    /// </summary>
    /// <param name="workspace">The workspace whose index is read.</param>
    /// <param name="encryptionPassword">A password when the index is encrypted; otherwise <see langword="null"/>.</param>
    /// <returns>The reader, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    public static SharpCoreDbCodeIndex For(Workspace workspace, string? encryptionPassword = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new SharpCoreDbCodeIndex(IndexPaths.For(workspace), encryptionPassword);
    }

    /// <summary>
    /// Reports what the index holds and how much of it can be trusted right now.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The status; never throws because the index is absent, empty or damaged.</returns>
    public ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(GetStatus());
    }

    /// <summary>
    /// Reports the status synchronously, which is what the maintainer does while it already holds the handle.
    /// </summary>
    /// <returns>The status, cached against the database file's stamp.</returns>
    internal IndexStatus GetStatus()
    {
        lock (_gate)
        {
            if (!_paths.DatabaseExists)
            {
                _cached = null;
                _cachedStamp = null;
                CloseHandle();
                LastReason = null;
                return IndexStatus.Missing(_paths.WorkspaceRoot);
            }

            var stamp = ReadStamp();
            if (_cached is { } cached && _cachedStamp is { } known && stamp == known)
            {
                return cached;
            }

            var status = ComputeStatus();
            _cached = status;
            _cachedStamp = stamp;
            return status;
        }
    }

    /// <summary>
    /// Drops the cached status, so the next read looks at the database again.
    /// </summary>
    internal void InvalidateStatus()
    {
        lock (_gate)
        {
            _cached = null;
            _cachedStamp = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            CloseHandle();
            _cached = null;
            _cachedStamp = null;
        }
    }

    private IDatabase OpenDatabase()
    {
        _lease ??= IndexHandleCache.Acquire(_paths, _encryptionPassword, createIfMissing: false);
        return _lease.Database.Database;
    }

    private void CloseHandle()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private FileStamp? ReadStamp()
    {
        try
        {
            var info = new FileInfo(_paths.DatabasePath);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IndexStatus ComputeStatus()
    {
        var root = _paths.WorkspaceRoot;
        var building = IndexWriterLock.IsHeldByAnotherWriter(_paths);

        Dictionary<string, string> meta;
        try
        {
            meta = IndexSchema.ReadMeta(OpenDatabase());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The engine's failure surface is not stable in a pre-release, and a status call is the one call
            // that may not throw: whatever it said is recorded as the reason and reported as an unreadable index.
            LastReason = Describe(ex);
            return Unreadable(root, building ? IndexFreshness.Building : IndexFreshness.Corrupt);
        }

        var view = new MetaView(meta);
        var version = view.Number("schema_version");
        var contract = view.Number("analyzer_contract");
        var storedRoot = view.Text("workspace_root");

        IndexFreshness freshness;
        string? reason = null;

        if (version is null || contract is null)
        {
            freshness = IndexFreshness.Corrupt;
            reason = "meta has no schema_version or analyzer_contract";
        }
        else if (version > IndexSchema.Version)
        {
            freshness = IndexFreshness.Corrupt;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"built with schema v{version}, this build understands v{IndexSchema.Version}");
        }
        else if (storedRoot is not null
                 && !string.Equals(storedRoot, _paths.ComparableWorkspaceRoot, StringComparison.Ordinal))
        {
            freshness = IndexFreshness.Corrupt;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"index belongs to '{storedRoot}', not to '{_paths.ComparableWorkspaceRoot}'");
        }
        else if (building)
        {
            freshness = IndexFreshness.Building;
            reason = "another SharpCoreAgent process is building the index";
        }
        else if (version < IndexSchema.Version || contract != IndexSchema.AnalyzerContract)
        {
            freshness = IndexFreshness.Stale;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"built with schema v{version} and analyzer {contract}, this build wants v{IndexSchema.Version} and analyzer {IndexSchema.AnalyzerContract}");
        }
        else
        {
            freshness = IndexFreshness.Fresh;
        }

        var staleFiles = 0;
        var skippedFiles = 0;
        var orphans = 0;

        if (freshness is IndexFreshness.Fresh or IndexFreshness.Stale)
        {
            try
            {
                (staleFiles, skippedFiles, orphans) = CountState(OpenDatabase());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A table the DDL never finished creating is the most likely cause, and the honest answer to
                // "what is in there" is then nothing at all.
                LastReason = Describe(ex);
                return Unreadable(root, IndexFreshness.Corrupt);
            }
        }

        if (freshness == IndexFreshness.Fresh)
        {
            if (string.Equals(view.Text("build_state"), IndexSchema.PartialState, StringComparison.Ordinal))
            {
                freshness = IndexFreshness.Partial;
                reason = "the last build stopped before the workspace was exhausted";
            }
            else if (view.Text("build_id") is null)
            {
                freshness = IndexFreshness.Stale;
                reason = "the index has never completed a build";
            }
            else if (staleFiles > 0)
            {
                freshness = IndexFreshness.Stale;
                reason = string.Create(CultureInfo.InvariantCulture, $"{staleFiles} file(s) changed since the last build");
            }
            else if (orphans > 0)
            {
                freshness = IndexFreshness.Stale;
                reason = string.Create(CultureInfo.InvariantCulture, $"{orphans} stored row(s) point at a file that is gone");
            }
        }

        LastReason = reason;

        var embedder = view.Text("embedder") ?? "off";
        var dimensions = view.Number("dimensions") ?? 0;
        var vectors = dimensions > 0 && !string.Equals(embedder, "off", StringComparison.Ordinal);

        return new IndexStatus
        {
            WorkspaceRoot = root,
            Freshness = freshness,
            Tier = string.Equals(view.Text("tier"), "rich", StringComparison.OrdinalIgnoreCase)
                ? IndexTier.Rich
                : IndexTier.Lean,
            Embedder = embedder,
            Dimensions = dimensions,
            Files = view.Number("file_count") ?? 0,
            Chunks = view.Number("chunk_count") ?? 0,
            Symbols = view.Number("symbol_count") ?? 0,
            Edges = view.Number("edge_count") ?? 0,
            StaleFiles = staleFiles,
            SkippedFiles = skippedFiles,
            OrphanRows = orphans,
            VectorSearchEnabled = vectors,
            LexicalFallback = !vectors,
            BuiltUtc = ParseUtc(view.Text("build_utc")),
            SizeBytes = MeasureSize(),
        };
    }

    private IndexStatus Unreadable(string root, IndexFreshness freshness) => new()
    {
        WorkspaceRoot = root,
        Freshness = freshness,
        Tier = IndexTier.Lean,
        Embedder = "off",
        Dimensions = 0,
        SizeBytes = MeasureSize(),
    };
    private static (int Stale, int Skipped, int Orphans) CountState(IDatabase database)
    {
        var ids = new HashSet<long>();
        var stale = 0;
        var skipped = 0;

        foreach (var row in database.ExecuteQuery("SELECT id, state FROM files"))
        {
            if (row["id"] is { } id)
            {
                ids.Add(Convert.ToInt64(id, CultureInfo.InvariantCulture));
            }

            var state = row["state"]?.ToString();
            if (string.Equals(state, "skipped", StringComparison.Ordinal))
            {
                skipped++;
            }
            else if (!string.Equals(state, "ok", StringComparison.Ordinal))
            {
                stale++;
            }
        }

        // An orphan is counted per stored row, because that is the number a verification reports and a rebuild
        // repairs. node_embeddings is left out on purpose: its node_id is a symbol id or a chunk id depending on
        // the row, so counting there would need a second lookup to mean anything.
        var orphans = 0;
        foreach (var table in OrphanedByFileTables)
        {
            var sql = string.Create(CultureInfo.InvariantCulture, $"SELECT file_id FROM {table}");
            foreach (var row in database.ExecuteQuery(sql))
            {
                if (row["file_id"] is { } fileId
                    && !ids.Contains(Convert.ToInt64(fileId, CultureInfo.InvariantCulture)))
                {
                    orphans++;
                }
            }
        }

        return (stale, skipped, orphans);
    }

    private static DateTimeOffset? ParseUtc(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private long MeasureSize()
    {
        try
        {
            if (!Directory.Exists(_paths.IndexDirectory))
            {
                return 0;
            }

            var lockName = Path.GetFileName(_paths.LockPath);

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(_paths.IndexDirectory))
            {
                // The lock is not part of the index; counting it would make the size flicker while a build runs.
                if (string.Equals(Path.GetFileName(file), lockName, StringComparison.Ordinal))
                {
                    continue;
                }

                total += new FileInfo(file).Length;
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Describe(Exception exception) =>
        string.Create(CultureInfo.InvariantCulture, $"{exception.GetType().Name}: {exception.Message}");

    private readonly record struct FileStamp(long Length, long WriteTicks);

    private sealed class MetaView(Dictionary<string, string> values)
    {
        public string? Text(string key) =>
            values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

        public int? Number(string key) =>
            Text(key) is { } text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
    }



}
