using System.Globalization;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The index schema: every table, every index and every <c>meta</c> key, in one place.
/// </summary>
/// <remarks>
/// The DDL is the contract in <c>specs/002-code-evidence-index/contracts/index-schema.md</c>, statement for
/// statement. It is idempotent — <c>IF NOT EXISTS</c> on every object and insert-if-absent for the seed rows —
/// so opening an index that already exists is the same call as creating one, which is the only way an
/// interrupted build can be resumed without a migration step.
/// <para>
/// Nothing here is public: a table name that escapes this assembly would be a second place the storage model
/// has to be kept in sync.
/// </para>
/// </remarks>
internal static class IndexSchema
{
    /// <summary>The schema version stored in <c>meta['schema_version']</c>; a mismatch rebuilds.</summary>
    internal const int Version = 1;

    /// <summary>The extractor version stored in <c>meta['parser_version']</c>.</summary>
    internal const int ParserVersion = 1;

    /// <summary>The chunking and tokenisation contract stored in <c>meta['analyzer_contract']</c>.</summary>
    /// <remarks>
    /// Raised to 3 when the writer stopped encoding vectors itself: it now binds the embedder's <c>float[]</c>
    /// and SharpCoreDB 2.1.1 stores it in the engine's own binary vector format. An index written through the
    /// old path holds the JSON text of the same array in every embedding cell, so reporting it as stale is
    /// what makes the next open rebuild those cells rather than read them through a second decoder.
    /// <para>
    /// 2 was the version raised when that JSON encoding arrived, at a time when a bound array was persisted
    /// as the literal text <c>System.Single[]</c>. The numbers therefore record the shape of the stored
    /// vectors, not the chunker, which has not changed between the two.
    /// </para>
    /// </remarks>
    internal const int AnalyzerContract = 3;

    /// <summary>The vector width; it must equal every <c>VECTOR(n)</c> declaration below.</summary>
    internal const int Dimensions = 512;

    /// <summary>The embedder identity of the default local embedder.</summary>
    internal const string DefaultEmbedder = "hashed-local/512";

    /// <summary>The value stored in <c>meta['tier']</c> for this release.</summary>
    internal const string DefaultTier = "lean";

    /// <summary>The value stored in <c>meta['quantization']</c>.</summary>
    internal const string DefaultQuantization = "int8";

    /// <summary>The <c>meta['build_state']</c> of a build that finished the workspace.</summary>
    internal const string CompleteState = "complete";

    /// <summary>The <c>meta['build_state']</c> of a build that stopped before the workspace was exhausted.</summary>
    internal const string PartialState = "partial";

    /// <summary>Gets the statement that creates one table or index, in execution order.</summary>
    internal static IReadOnlyList<string> Statements { get; } =
    [
        """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS files (
            id           INTEGER PRIMARY KEY AUTO,
            path         TEXT NOT NULL,
            language     TEXT NOT NULL,
            size_bytes   INTEGER NOT NULL,
            mtime_utc    TEXT NOT NULL,
            sha256       TEXT NOT NULL,
            state        TEXT NOT NULL CHECK (state IN ('ok','skipped','stale','error')),
            skip_reason  TEXT,
            chunk_count  INTEGER NOT NULL DEFAULT 0,
            symbol_count INTEGER NOT NULL DEFAULT 0,
            indexed_utc  TEXT NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_files_path ON files (path)",
        "CREATE INDEX IF NOT EXISTS idx_files_sha  ON files (sha256)",
        $"""
        CREATE TABLE IF NOT EXISTS chunks (
            id          INTEGER PRIMARY KEY AUTO,
            file_id     INTEGER NOT NULL,
            start_line  INTEGER NOT NULL,
            end_line    INTEGER NOT NULL,
            kind        TEXT NOT NULL,
            symbol_key  TEXT,
            symbol_name TEXT,
            text        TEXT NOT NULL,
            tokens      TEXT NOT NULL,
            sha256      TEXT NOT NULL,
            embedding   VECTOR({Dimensions})
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_chunks_file   ON chunks (file_id)",
        "CREATE INDEX IF NOT EXISTS idx_chunks_symbol ON chunks (symbol_key)",
        """
        CREATE TABLE IF NOT EXISTS symbols (
            id             INTEGER PRIMARY KEY AUTO,
            file_id        INTEGER NOT NULL,
            key            TEXT NOT NULL,
            name           TEXT NOT NULL,
            qualified_name TEXT NOT NULL,
            kind           TEXT NOT NULL,
            namespace      TEXT,
            container      TEXT,
            start_line     INTEGER NOT NULL,
            end_line       INTEGER NOT NULL,
            signature      TEXT,
            doc            TEXT,
            community      TEXT,
            centrality     REAL NOT NULL DEFAULT 0
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_symbols_key       ON symbols (key)",
        "CREATE INDEX IF NOT EXISTS idx_symbols_name      ON symbols (name COLLATE NOCASE)",
        "CREATE INDEX IF NOT EXISTS idx_symbols_file      ON symbols (file_id)",
        "CREATE INDEX IF NOT EXISTS idx_symbols_community ON symbols (community)",
        """
        CREATE TABLE IF NOT EXISTS edges (
            id      INTEGER PRIMARY KEY AUTO,
            source  INTEGER NOT NULL,
            target  INTEGER NOT NULL,
            rel     TEXT NOT NULL CHECK (rel IN ('calls','references','inherits','implements','imports','declares','doc_mentions')),
            weight  REAL NOT NULL,
            file_id INTEGER
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_edges_source ON edges (source)",
        "CREATE INDEX IF NOT EXISTS idx_edges_target ON edges (target)",
        $"""
        CREATE TABLE IF NOT EXISTS node_embeddings (
            id        INTEGER PRIMARY KEY AUTO,
            node_id   INTEGER NOT NULL,
            label     TEXT NOT NULL,
            embedding VECTOR({Dimensions})
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_node_embeddings_node ON node_embeddings (node_id)",
        """
        CREATE TABLE IF NOT EXISTS docs (
            id         INTEGER PRIMARY KEY AUTO,
            file_id    INTEGER,
            heading    TEXT,
            symbol_key TEXT,
            start_line INTEGER NOT NULL,
            end_line   INTEGER NOT NULL,
            text       TEXT NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_docs_symbol ON docs (symbol_key)",
    ];

    /// <summary>Gets the tables the schema owns, in creation order.</summary>
    internal static IReadOnlyList<string> Tables { get; } =
        ["meta", "files", "chunks", "symbols", "edges", "node_embeddings", "docs"];

    /// <summary>
    /// Creates every object and seed row that is missing.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <param name="paths">The paths the database belongs to; the root is seeded into <c>meta</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static void EnsureCreated(IDatabase database, IndexPaths paths)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);

        // One batch, so an interrupted creation leaves either the whole schema or none of it.
        database.ExecuteBatchSQL(Statements);

        SeedMeta(database, paths);
    }

    /// <summary>
    /// Decides whether the stored rows were derived by rules this build does not share.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>
    /// <see langword="true"/> when the stored parser or analyzer contract differs from this build's.
    /// </returns>
    /// <remarks>
    /// An incremental pass would keep every file whose size, timestamp and hash still match, which is exactly
    /// the wrong answer here: those rows are current with the file and stale with the code. The maintainer
    /// empties the tables instead, so "the contract changed" means "derive everything again", in the one place
    /// <c>contracts/index-schema.md</c> describes.
    /// <para>
    /// <c>schema_version</c> is deliberately not one of the two: a newer one is refused as corrupt rather than
    /// quietly rebuilt by an older build, and the version the code knows has no migration to run.
    /// </para>
    /// </remarks>
    internal static bool RowsWereDerivedByAnotherContract(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var meta = ReadMeta(database);
        return Differs(meta, "parser_version", ParserVersion) || Differs(meta, "analyzer_contract", AnalyzerContract);

        static bool Differs(Dictionary<string, string> meta, string key, int expected) =>
            !meta.TryGetValue(key, out var stored)
            || !string.Equals(stored, expected.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the whole <c>meta</c> table.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>Key to value; empty when the table exists but was never seeded.</returns>
    internal static Dictionary<string, string> ReadMeta(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in database.ExecuteQuery("SELECT key, value FROM meta"))
        {
            if (row.TryGetValue("key", out var key) && row.TryGetValue("value", out var value)
                && key?.ToString() is { Length: > 0 } name)
            {
                meta[name] = value?.ToString() ?? string.Empty;
            }
        }

        return meta;
    }

    private static void SeedMeta(IDatabase database, IndexPaths paths)
    {
        var existing = ReadMeta(database);

        foreach (var (key, value) in CreateSeedValues(paths))
        {
            if (existing.ContainsKey(key))
            {
                continue;
            }

            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["@key"] = key,
                ["@value"] = value,
            };

            database.ExecuteSQL("INSERT INTO meta (key, value) VALUES (@key, @value)", parameters);
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> CreateSeedValues(IndexPaths paths)
    {
        yield return Pair("schema_version", Version.ToString(CultureInfo.InvariantCulture));
        yield return Pair("parser_version", ParserVersion.ToString(CultureInfo.InvariantCulture));
        yield return Pair("analyzer_contract", AnalyzerContract.ToString(CultureInfo.InvariantCulture));
        yield return Pair("tier", DefaultTier);
        yield return Pair("embedder", DefaultEmbedder);
        yield return Pair("dimensions", Dimensions.ToString(CultureInfo.InvariantCulture));
        yield return Pair("quantization", DefaultQuantization);
        yield return Pair("workspace_root", paths.ComparableWorkspaceRoot);

        // Blank until the first complete build: the reader treats an empty build id as "never built", which
        // is the only truthful value for a schema that exists because someone opened the index.
        yield return Pair("build_id", string.Empty);
        yield return Pair("build_utc", string.Empty);
        yield return Pair("build_state", string.Empty);

        yield return Pair("file_count", "0");
        yield return Pair("chunk_count", "0");
        yield return Pair("symbol_count", "0");
        yield return Pair("edge_count", "0");
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);
}
