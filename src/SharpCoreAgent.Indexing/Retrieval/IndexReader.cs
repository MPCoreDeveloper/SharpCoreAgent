using System.Globalization;
using SharpCoreDB.Interfaces;
using SharpCoreDB.Search.Lexical;
using SharpCoreDB.VectorSearch;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// Loads one build into memory and keeps it until that build is replaced.
/// </summary>
/// <remarks>
/// The cache key is <c>meta['build_id']</c>: a rebuild changes it, so the next query reloads, while a query in
/// the same build costs one small read and no table scan. Status reads use their own path
/// (<see cref="SharpCoreDbCodeIndex"/>); this type exists for the five retrieval legs, which cannot afford a
/// database round-trip per candidate.
/// </remarks>
internal sealed class IndexReader
{
    private const string ChunkQuery =
        "SELECT id, file_id, start_line, end_line, kind, symbol_key, symbol_name, text, sha256, embedding FROM chunks ORDER BY id";

    private const string SymbolQuery =
        "SELECT id, file_id, key, name, qualified_name, kind, start_line, end_line, signature, community, centrality FROM symbols ORDER BY id";

    private readonly Lock _gate = new();

    private IndexSnapshot? _snapshot;

    /// <summary>
    /// Creates a reader with nothing loaded.
    /// </summary>
    /// <returns>The reader; the first <see cref="Load"/> does the work.</returns>
    public static IndexReader Create() => new();

    /// <summary>
    /// Returns the snapshot of the build the database currently holds.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>The snapshot, reloaded when <c>build_id</c> changed since the last call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public IndexSnapshot Load(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var buildId = ReadBuildId(database);

        lock (_gate)
        {
            if (_snapshot is { } cached && string.Equals(cached.BuildId, buildId, StringComparison.Ordinal))
            {
                return cached;
            }

            var loaded = LoadSnapshot(database, buildId);
            _snapshot = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// Drops the loaded snapshot, so the next call reads the tables again.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    /// <summary>
    /// Reads the build id, which is the cache key.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>The id, or the empty string for an index that was never built.</returns>
    private static string ReadBuildId(IDatabase database)
    {
        var rows = IndexSql.Query(
            database,
            "SELECT value FROM meta WHERE key = @key",
            IndexSql.Parameters(("@key", "build_id")));

        return rows.Count == 0 ? string.Empty : IndexSql.AsText(rows[0].GetValueOrDefault("value"));
    }

    private static IndexSnapshot LoadSnapshot(IDatabase database, string buildId)
    {
        var files = LoadFiles(database);
        var paths = files.ToDictionary(entry => entry.Key, entry => entry.Value.Path);
        var meta = IndexSchema.ReadMeta(database);

        return new IndexSnapshot
        {
            BuildId = buildId,
            Paths = paths,
            Files = files,
            Chunks = LoadChunks(database, paths),
            Symbols = LoadSymbols(database, paths),
            Edges = LoadEdges(database),
            DocumentedSymbols = LoadDocumentedSymbols(database),
            Lexical = LoadLexical(database),
            Embedder = meta.TryGetValue("embedder", out var embedder) ? embedder : "off",
            Dimensions = meta.TryGetValue("dimensions", out var dimensions)
                && int.TryParse(dimensions, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                    ? width
                    : 0,
        };
    }

    private static Dictionary<long, FileRow> LoadFiles(IDatabase database)
    {
        var files = new Dictionary<long, FileRow>();

        foreach (var row in IndexSql.Query(database, "SELECT id, path, language, state, mtime_utc FROM files ORDER BY id"))
        {
            var id = IndexSql.AsLong(row.GetValueOrDefault("id"));

            files[id] = new FileRow
            {
                Id = id,
                Path = IndexSql.AsText(row.GetValueOrDefault("path")),
                Language = IndexSql.AsText(row.GetValueOrDefault("language")),
                State = IndexSql.AsText(row.GetValueOrDefault("state")),
                ModifiedUtc = IndexTimestamps.Parse(IndexSql.AsOptionalText(row.GetValueOrDefault("mtime_utc"))),
            };
        }

        return files;
    }

    /// <summary>
    /// Reads the declaration keys the stored documentation points at.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>The keys; empty when no documentation row names a declaration.</returns>
    private static HashSet<string> LoadDocumentedSymbols(IDatabase database)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in IndexSql.Query(database, "SELECT symbol_key FROM docs"))
        {
            if (IndexSql.AsOptionalText(row.GetValueOrDefault("symbol_key")) is { } key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static Dictionary<long, ChunkRow> LoadChunks(IDatabase database, Dictionary<long, string> paths)
    {
        var chunks = new Dictionary<long, ChunkRow>();

        foreach (var row in IndexSql.Query(database, ChunkQuery))
        {
            var id = IndexSql.AsLong(row.GetValueOrDefault("id"));
            var fileId = IndexSql.AsLong(row.GetValueOrDefault("file_id"));

            chunks[id] = new ChunkRow
            {
                Id = id,
                FileId = fileId,
                Path = paths.TryGetValue(fileId, out var path) ? path : string.Empty,
                StartLine = (int)IndexSql.AsLong(row.GetValueOrDefault("start_line")),
                EndLine = (int)IndexSql.AsLong(row.GetValueOrDefault("end_line")),
                Kind = IndexSql.AsText(row.GetValueOrDefault("kind")),
                SymbolKey = IndexSql.AsOptionalText(row.GetValueOrDefault("symbol_key")),
                SymbolName = IndexSql.AsOptionalText(row.GetValueOrDefault("symbol_name")),
                Text = IndexSql.AsText(row.GetValueOrDefault("text")),
                Sha8 = ShortHash(IndexSql.AsText(row.GetValueOrDefault("sha256"))),
                Vector = AsVector(row.GetValueOrDefault("embedding")),
            };
        }

        return chunks;
    }

    private static Dictionary<long, SymbolRow> LoadSymbols(IDatabase database, Dictionary<long, string> paths)
    {
        var symbols = new Dictionary<long, SymbolRow>();

        foreach (var row in IndexSql.Query(database, SymbolQuery))
        {
            var id = IndexSql.AsLong(row.GetValueOrDefault("id"));
            var fileId = IndexSql.AsLong(row.GetValueOrDefault("file_id"));

            symbols[id] = new SymbolRow
            {
                Id = id,
                FileId = fileId,
                Key = IndexSql.AsText(row.GetValueOrDefault("key")),
                Name = IndexSql.AsText(row.GetValueOrDefault("name")),
                QualifiedName = IndexSql.AsText(row.GetValueOrDefault("qualified_name")),
                Kind = IndexSql.AsText(row.GetValueOrDefault("kind")),
                StartLine = (int)IndexSql.AsLong(row.GetValueOrDefault("start_line")),
                EndLine = (int)IndexSql.AsLong(row.GetValueOrDefault("end_line")),
                Path = paths.TryGetValue(fileId, out var path) ? path : string.Empty,
                Signature = IndexSql.AsOptionalText(row.GetValueOrDefault("signature")),
                Community = IndexSql.AsOptionalText(row.GetValueOrDefault("community")),
                Centrality = ToDouble(row.GetValueOrDefault("centrality")),
            };
        }

        return symbols;
    }

    private static List<EdgeRow> LoadEdges(IDatabase database)
    {
        var edges = new List<EdgeRow>();

        foreach (var row in IndexSql.Query(database, "SELECT source, target, rel, weight FROM edges ORDER BY id"))
        {
            edges.Add(new EdgeRow(
                IndexSql.AsLong(row.GetValueOrDefault("source")),
                IndexSql.AsLong(row.GetValueOrDefault("target")),
                IndexSql.AsText(row.GetValueOrDefault("rel")),
                ToDouble(row.GetValueOrDefault("weight"))));
        }

        return edges;
    }

    /// <summary>
    /// Builds the BM25 index from the stored token strings, in id order so two loads score identically.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>The lexical index, ready for <c>Search</c>.</returns>
    private static FullTextIndex LoadLexical(IDatabase database)
    {
        var lexical = new FullTextIndex();

        foreach (var row in IndexSql.Query(database, "SELECT id, tokens FROM chunks ORDER BY id"))
        {
            lexical.Add(
                IndexSql.AsLong(row.GetValueOrDefault("id")),
                IndexSql.AsText(row.GetValueOrDefault("tokens")));
        }

        return lexical;
    }

    private static double ToDouble(object? value) =>
        value is null or DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a <c>VECTOR(n)</c> column.
    /// </summary>
    /// <param name="value">The raw column value.</param>
    /// <returns>The vector, or <see langword="null"/> when the column was never written.</returns>
    /// <remarks>
    /// The column hands back a <c>float[]</c> once a real vector was written. The text form is still accepted,
    /// because an index built by the previous writer holds the literal string <c>System.Single[]</c>, and a reader
    /// that threw on it would turn a stale index into a crash instead of a passage without a vector.
    /// </remarks>
    private static float[]? AsVector(object? value) => value switch
    {
        float[] vector when vector.Length > 0 => vector,
        string text when text.StartsWith('[') => VectorSerializer.FromJson(text),
        Array array => [.. array.Cast<object?>().Select(item => Convert.ToSingle(item, CultureInfo.InvariantCulture))],
        _ => null,
    };

    private static string ShortHash(string sha256) => sha256.Length > 8 ? sha256[..8] : sha256;
}
