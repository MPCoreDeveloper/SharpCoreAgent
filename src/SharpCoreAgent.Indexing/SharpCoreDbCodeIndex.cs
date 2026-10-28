using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Embedding;
using SharpCoreAgent.Indexing.Ranking;
using SharpCoreAgent.Indexing.Retrieval;
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
/// The retrieval methods of <see cref="ICodeIndex"/> arrive with the legs that answer them; this type grows into that
/// interface in M3 instead of shipping stubs that would silently return nothing. <see cref="Search"/> is the first of
/// them, composed from the two legs, the ranker and the budget.
/// </remarks>
internal sealed class SharpCoreDbCodeIndex : ICodeIndex, IDisposable
{
    private static readonly string[] OrphanedByFileTables = ["chunks", "symbols", "docs"];

    /// <summary>The most declarations one path-targeted context lists.</summary>
    private const int MaxDeclarations = 50;

    private readonly Lock _gate = new();
    private readonly IndexPaths _paths;
    private readonly string? _encryptionPassword;
    private readonly IndexReader _reader = IndexReader.Create();
    private readonly ICodeTextEmbedder _embedder = HashedCodeEmbedder.CreateDefault();

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

    /// <summary>
    /// Answers one search: both retrieval legs, the ranker and the selection budget, in that order.
    /// </summary>
    /// <param name="query">A validated request from <see cref="CodeQuery.Create"/>.</param>
    /// <param name="nowUtc">The moment the query runs, which only the recency boost reads.</param>
    /// <param name="maxChars">The character allowance for the whole answer.</param>
    /// <returns>The blocks that fit, best first; empty when the workspace has no index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <remarks>
    /// This is the composition the five tools share, kept in one place so the tool that ships in T021 renders an
    /// answer instead of assembling one. A missing index returns an empty list rather than throwing, because absence
    /// is a value everywhere else in this assembly.
    /// </remarks>
    internal IReadOnlyList<CodeEvidence> Search(CodeQuery query, DateTimeOffset nowUtc, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_paths.DatabaseExists)
        {
            return [];
        }

        var database = OpenDatabase();
        var snapshot = _reader.Load(database);

        if (snapshot.ChunkCount == 0)
        {
            return [];
        }

        var ranked = EvidenceRanker.Rank(
            snapshot,
            query,
            LexicalRetriever.Search(snapshot, query.Text, query.K),
            VectorRetriever.Search(snapshot, _embedder, query.Text, query.K).Matches,
            nowUtc);

        return EvidenceBudget.Apply(ranked, maxChars).Blocks;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeEvidence>> SearchAsync(CodeQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Search(query, DateTimeOffset.UtcNow, EvidenceBudget.DefaultMaxChars));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeSymbolRef>> FindSymbolsAsync(
        string name,
        string? kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = TrySnapshot();
        return ValueTask.FromResult(snapshot is null
            ? []
            : SymbolRetriever.Search(snapshot, name, kind, SymbolRetriever.MaxResults));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CodeEvidence>> FindReferencesAsync(
        string symbol,
        ReferenceDirection direction,
        int maxResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = TrySnapshot();
        return ValueTask.FromResult(snapshot is null
            ? []
            : ReferenceRetriever.Find(snapshot, symbol, direction, maxResults));
    }

    /// <inheritdoc />
    public ValueTask<CodeContext> GetContextAsync(
        string target,
        int hops,
        int k,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = TrySnapshot();
        return ValueTask.FromResult(snapshot is null
            ? CodeContext.Empty(target)
            : Context(snapshot, target, hops, k));
    }

    /// <summary>
    /// Builds one neighbourhood answer from a loaded snapshot.
    /// </summary>
    /// <param name="snapshot">The build being read.</param>
    /// <param name="target">A workspace-relative path, or a symbol name.</param>
    /// <param name="hops">How many edges to follow.</param>
    /// <param name="k">How many neighbours to return.</param>
    /// <returns>
    /// The centre, its own passage and its neighbours; an empty context when the target resolves to nothing.
    /// </returns>
    /// <remarks>
    /// The file is resolved separately from the seed so the answer can tell a path target from a symbol target:
    /// naming a file asks what the file declares, and that list belongs in front of the neighbourhood rather than
    /// behind it.
    /// </remarks>
    private static CodeContext Context(IndexSnapshot snapshot, string target, int hops, int k)
    {
        if (NeighbourhoodRetriever.ResolveSeed(snapshot, target) is not { } seed)
        {
            return CodeContext.Empty(target);
        }

        var file = NeighbourhoodRetriever.ResolveFile(snapshot, target);
        var neighbourhood = NeighbourhoodRetriever.Expand(snapshot, seed, hops, k);
        var neighbours = new List<CodeEvidence>(neighbourhood.Nodes.Count);

        foreach (var neighbour in neighbourhood.Nodes)
        {
            if (EvidenceBlocks.ForSymbol(snapshot, neighbour.Row, neighbour.Relation, neighbour.Weight) is { } block)
            {
                neighbours.Add(block);
            }
        }

        return new CodeContext
        {
            Target = target.Trim(),
            Subject = SymbolRetriever.ToRef(snapshot, seed),
            Declarations = file is null ? [] : SymbolRetriever.InFile(snapshot, file.Id, MaxDeclarations),
            Focus = EvidenceBlocks.ForSymbol(snapshot, seed, null, seed.Centrality),
            Neighbours = neighbours,
            Considered = neighbourhood.Considered,
            MinWeight = neighbours.Count == 0 ? 0 : NeighbourhoodRetriever.MinWeight,
        };
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

    /// <summary>
    /// Loads the snapshot the graph and symbol legs answer from, or reports that there is nothing to read.
    /// </summary>
    /// <returns>The snapshot, or <see langword="null"/> when the workspace has no index or an empty one.</returns>
    private IndexSnapshot? TrySnapshot()
    {
        if (!_paths.DatabaseExists)
        {
            return null;
        }

        var snapshot = _reader.Load(OpenDatabase());
        return snapshot.ChunkCount == 0 ? null : snapshot;
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

    /// <summary>
    /// Decides what the stored meta alone says about the index.
    /// </summary>
    /// <param name="view">The meta table, as read.</param>
    /// <param name="comparableRoot">The workspace the index is expected to describe.</param>
    /// <param name="building">Whether another process holds the writer lock.</param>
    /// <returns>The freshness and, when it is not fresh, the reason a developer can act on.</returns>
    private static (IndexFreshness Freshness, string? Reason) DescribeFreshness(
        MetaView view,
        string comparableRoot,
        bool building)
    {
        var version = view.Number("schema_version");
        var contract = view.Number("analyzer_contract");
        var storedRoot = view.Text("workspace_root");

        if (version is null || contract is null)
        {
            return (IndexFreshness.Corrupt, "meta has no schema_version or analyzer_contract");
        }

        if (version > IndexSchema.Version)
        {
            return (IndexFreshness.Corrupt, string.Create(
                CultureInfo.InvariantCulture,
                $"built with schema v{version}, this build understands v{IndexSchema.Version}"));
        }

        if (storedRoot is not null && !string.Equals(storedRoot, comparableRoot, StringComparison.Ordinal))
        {
            return (IndexFreshness.Corrupt, string.Create(
                CultureInfo.InvariantCulture,
                $"index belongs to '{storedRoot}', not to '{comparableRoot}'"));
        }

        if (building)
        {
            return (IndexFreshness.Building, "another SharpCoreAgent process is building the index");
        }

        if (version < IndexSchema.Version || contract != IndexSchema.AnalyzerContract)
        {
            return (IndexFreshness.Stale, string.Create(
                CultureInfo.InvariantCulture,
                $"built with schema v{version} and analyzer {contract}, this build wants v{IndexSchema.Version} and analyzer {IndexSchema.AnalyzerContract}"));
        }

        return (IndexFreshness.Fresh, null);
    }

    /// <summary>
    /// Downgrades a fresh index whose last build did not finish, or whose files moved under it.
    /// </summary>
    /// <param name="view">The meta table, as read.</param>
    /// <param name="freshness">What the schema version decided.</param>
    /// <param name="reason">The reason that decision produced, if any.</param>
    /// <param name="staleFiles">How many files changed since the last build.</param>
    /// <param name="orphans">How many stored rows point at a file that is gone.</param>
    /// <returns>The freshness and reason after the stored state is taken into account.</returns>
    private static (IndexFreshness Freshness, string? Reason) ApplyBuildState(
        MetaView view,
        IndexFreshness freshness,
        string? reason,
        int staleFiles,
        int orphans)
    {
        if (freshness != IndexFreshness.Fresh)
        {
            return (freshness, reason);
        }

        if (string.Equals(view.Text("build_state"), IndexSchema.PartialState, StringComparison.Ordinal))
        {
            return (IndexFreshness.Partial, "the last build stopped before the workspace was exhausted");
        }

        if (view.Text("build_id") is null)
        {
            return (IndexFreshness.Stale, "the index has never completed a build");
        }

        if (staleFiles > 0)
        {
            return (IndexFreshness.Stale, string.Create(
                CultureInfo.InvariantCulture,
                $"{staleFiles} file(s) changed since the last build"));
        }

        if (orphans > 0)
        {
            return (IndexFreshness.Stale, string.Create(
                CultureInfo.InvariantCulture,
                $"{orphans} stored row(s) point at a file that is gone"));
        }

        return (IndexFreshness.Fresh, null);
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
        var (freshness, reason) = DescribeFreshness(view, _paths.ComparableWorkspaceRoot, building);

        var staleFiles = 0;
        var skippedFiles = 0;
        var orphans = 0;
        var communities = 0;
        var centers = 0;

        if (freshness is IndexFreshness.Fresh or IndexFreshness.Stale)
        {
            try
            {
                (staleFiles, skippedFiles, orphans) = CountState(OpenDatabase());
                (communities, centers) = CountGraph(OpenDatabase());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A table the DDL never finished creating is the most likely cause, and the honest answer to
                // "what is in there" is then nothing at all.
                LastReason = Describe(ex);
                return Unreadable(root, IndexFreshness.Corrupt);
            }
        }

        (freshness, reason) = ApplyBuildState(view, freshness, reason, staleFiles, orphans);
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
            Communities = communities,
            Centers = centers,
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

    /// <summary>
    /// Counts the communities the last build assigned, and how many of them named a centre.
    /// </summary>
    /// <param name="database">The index to read.</param>
    /// <returns>The number of distinct labels, and of labels whose members carry a degree centrality.</returns>
    /// <remarks>
    /// A centre is the highest-degree declaration of its community, so a community that has a relation at all has
    /// one. Counting the two separately is deliberate: a label written without a centrality is the drift a single
    /// number would hide, and reading the columns is cheaper than asking Louvain again for an answer the build
    /// already stored.
    /// </remarks>
    private static (int Communities, int Centers) CountGraph(IDatabase database)
    {
        var communities = new HashSet<string>(StringComparer.Ordinal);
        var centers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in database.ExecuteQuery("SELECT community, centrality FROM symbols"))
        {
            var community = IndexSql.AsOptionalText(row.GetValueOrDefault("community"));
            if (string.IsNullOrEmpty(community))
            {
                continue;
            }

            communities.Add(community);

            if (Centrality(row.GetValueOrDefault("centrality")) > 0)
            {
                centers.Add(community);
            }
        }

        return (communities.Count, centers.Count);
    }

    /// <summary>
    /// Reads a stored centrality, which the engine hands back as whatever numeric type it stored.
    /// </summary>
    /// <param name="value">The column value.</param>
    /// <returns>The number, or 0 when there is none.</returns>
    private static double Centrality(object? value) =>
        value is null or DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);

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
