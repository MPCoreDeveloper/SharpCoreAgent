using SharpCoreAgent.Indexing.Embedding;
using SharpCoreAgent.Indexing.Extraction;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// One file the build just wrote, with the relations that still have to be resolved into edges.
/// </summary>
/// <remarks>
/// Relations are named by symbol keys and by plain names, so they cannot become edges until every file of the
/// build has its symbols stored. Carrying them out of the write step is what lets the build resolve them once,
/// against the whole symbol table, instead of guessing at insert time.
/// </remarks>
internal sealed record WrittenFile
{
    /// <summary>Gets the <c>files.id</c> the rows were written under.</summary>
    public required long FileId { get; init; }

    /// <summary>Gets the workspace-relative path that was written.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the relations the extractor found, still named rather than numbered.</summary>
    public required IReadOnlyList<SyntaxRelation> Relations { get; init; }
}

/// <summary>
/// Writes the rows of exactly one file: its declaration, its passages, their vectors and its documentation.
/// </summary>
/// <remarks>
/// The unit of work is one file, deleted and re-inserted inside one batch, because that is the largest thing
/// that can fail independently: a build that dies on file 40 leaves files 1–39 committed and file 40 as it was.
/// <para>
/// Nothing here decides <em>whether</em> to write — the build does that with size, timestamp and hash. This type
/// only turns a file's text into rows, which is the part that changes when the parser does.
/// </para>
/// </remarks>
internal sealed class IndexFileWriter
{
    /// <summary>The bound parameter every statement in this file uses for the owning file id.</summary>
    /// <remarks>
    /// A constant rather than a repeated literal: the name appears in ten statements, and a typo in one of them
    /// would bind nothing and insert a row nobody asked for.
    /// </remarks>
    private const string FileParameter = "@file";

    /// <summary>The bound parameter that identifies a file by its workspace-relative path.</summary>
    private const string PathParameter = "@path";

    private readonly IDatabase _database;
    private readonly ISyntaxFactsExtractor _extractor;
    private readonly ICodeTextEmbedder _embedder;

    private IndexFileWriter(IDatabase database, ISyntaxFactsExtractor extractor, ICodeTextEmbedder embedder)
    {
        _database = database;
        _extractor = extractor;
        _embedder = embedder;
    }

    /// <summary>
    /// Creates a writer for one open index database.
    /// </summary>
    /// <param name="database">The database the rows are written to.</param>
    /// <param name="extractor">The extractor that turns text into facts; the heuristic one by default.</param>
    /// <param name="embedder">The embedder; the local hashed one by default.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IndexFileWriter For(
        IDatabase database,
        ISyntaxFactsExtractor? extractor = null,
        ICodeTextEmbedder? embedder = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        return new IndexFileWriter(
            database,
            extractor ?? HeuristicSyntaxFactsExtractor.Create(),
            embedder ?? HashedCodeEmbedder.CreateDefault());
    }

    /// <summary>
    /// Extracts one file and replaces its rows.
    /// </summary>
    /// <param name="file">The scanned file, whose path and language label the row carries.</param>
    /// <param name="content">The file's hash and text.</param>
    /// <param name="previousFileId">The id the file had before, or <see langword="null"/> for a new file.</param>
    /// <param name="indexedUtc">The timestamp to record in <c>files.indexed_utc</c>.</param>
    /// <param name="cancellationToken">Cancels between passages.</param>
    /// <returns>The written file id and the relations the build still has to resolve.</returns>
    public WrittenFile Write(
        ScannedFile file,
        IndexFileContent content,
        long? previousFileId,
        string indexedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);

        var facts = _extractor.Extract(file, content.Text, cancellationToken);
        var passages = PassageChunker.Chunk(content.Text, facts.Symbols, cancellationToken);

        return RunInBatch(() =>
        {
            if (previousFileId is { } previous)
            {
                DeleteRows(previous);
            }

            var fileId = InsertFile(file, content, passages.Count, facts.Symbols.Count, indexedUtc);
            var symbolIds = InsertSymbols(fileId, facts.Symbols);
            InsertPassages(fileId, passages, cancellationToken);
            InsertDocs(fileId, facts);
            InsertSymbolVectors(symbolIds, facts.Symbols);

            return new WrittenFile { FileId = fileId, Path = file.Path, Relations = facts.Relations };
        });
    }

    /// <summary>
    /// Records a file the build did not extract, keeping its reason.
    /// </summary>
    /// <param name="file">The scanned file.</param>
    /// <param name="previousFileId">The id the file had before, or <see langword="null"/>.</param>
    /// <param name="state"><see cref="IndexFileStates.Skipped"/> or <see cref="IndexFileStates.Error"/>.</param>
    /// <param name="reason">Why the file has no rows of its own.</param>
    /// <param name="indexedUtc">The timestamp to record.</param>
    public void WritePlaceholder(
        ScannedFile file,
        long? previousFileId,
        string state,
        string reason,
        string indexedUtc)
    {
        ArgumentNullException.ThrowIfNull(file);

        RunInBatch(() =>
        {
            if (previousFileId is { } previous)
            {
                DeleteRows(previous);
            }

            IndexSql.Execute(
                _database,
                """
                INSERT INTO files (path, language, size_bytes, mtime_utc, sha256, state, skip_reason, chunk_count, symbol_count, indexed_utc)
                VALUES (@path, @language, @size, @mtime, @sha, @state, @reason, 0, 0, @indexed)
                """,
                IndexSql.Parameters(
                    (PathParameter, file.Path),
                    ("@language", file.Language),
                    ("@size", file.SizeBytes),
                    ("@mtime", IndexTimestamps.Format(file.ModifiedUtc)),
                    ("@sha", string.Empty),
                    ("@state", state),
                    ("@reason", reason),
                    ("@indexed", indexedUtc)));

            return true;
        });
    }

    /// <summary>
    /// Drops every row that belonged to one file.
    /// </summary>
    /// <param name="fileId">The id to drop.</param>
    public void Delete(long fileId) => RunInBatch(() =>
    {
        DeleteRows(fileId);
        return true;
    });

    /// <summary>
    /// Inserts the resolved relations of one build as a single batch.
    /// </summary>
    /// <param name="edges">The edges to store, already deduplicated and resolved to ids.</param>
    public void WriteEdges(IReadOnlyList<IndexEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        if (edges.Count == 0)
        {
            return;
        }

        RunInBatch(() =>
        {
            foreach (var edge in edges)
            {
                IndexSql.Execute(
                    _database,
                    "INSERT INTO edges (source, target, rel, weight, file_id) VALUES (@source, @target, @rel, @weight, @file)",
                    IndexSql.Parameters(
                        ("@source", edge.Source),
                        ("@target", edge.Target),
                        ("@rel", edge.Relation),
                        ("@weight", (double)edge.Weight),
                        (FileParameter, edge.FileId)));
            }

            return true;
        });
    }

    /// <summary>
    /// Overwrites <c>meta</c> keys as a single batch.
    /// </summary>
    /// <param name="values">The keys and their new values; a key that does not exist is left alone.</param>
    public void WriteMeta(IReadOnlyList<(string Key, string Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return;
        }

        RunInBatch(() =>
        {
            foreach (var (key, value) in values)
            {
                IndexSql.Execute(
                    _database,
                    "UPDATE meta SET value = @value WHERE key = @key",
                    IndexSql.Parameters(("@key", key), ("@value", value)));
            }

            return true;
        });
    }

    /// <summary>
    /// Runs one unit of work as a single batch, abandoning it when anything throws.
    /// </summary>
    /// <typeparam name="TResult">What the work returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <returns>Whatever <paramref name="work"/> returned.</returns>
    /// <remarks>
    /// <c>CancelBatchUpdate</c> after a committed <c>EndBatchUpdate</c> is not called, so the two are strictly
    /// paired: a caller that sees an exception knows no part of the file reached the database.
    /// </remarks>
    private TResult RunInBatch<TResult>(Func<TResult> work)
    {
        _database.BeginBatchUpdate();
        try
        {
            var result = work();
            _database.EndBatchUpdate();
            return result;
        }
        catch (Exception)
        {
            _database.CancelBatchUpdate();
            throw;
        }
    }

    /// <summary>
    /// Removes every row that belonged to one file, edges pointing at it included.
    /// </summary>
    /// <param name="fileId">The id being replaced.</param>
    private void DeleteRows(long fileId)
    {
        var parameters = IndexSql.Parameters((FileParameter, fileId));

        // An edge that pointed at one of this file's symbols would dangle the moment the symbols go, so the
        // incoming side is cleared here and not only the outgoing rows this file owned.
        IndexSql.Execute(
            _database,
            "DELETE FROM node_embeddings WHERE node_id IN (SELECT id FROM symbols WHERE file_id = @file)",
            parameters);
        IndexSql.Execute(_database, "DELETE FROM edges WHERE file_id = @file", parameters);
        IndexSql.Execute(
            _database,
            "DELETE FROM edges WHERE target IN (SELECT id FROM symbols WHERE file_id = @file)",
            parameters);
        IndexSql.Execute(_database, "DELETE FROM chunks WHERE file_id = @file", parameters);
        IndexSql.Execute(_database, "DELETE FROM symbols WHERE file_id = @file", parameters);
        IndexSql.Execute(_database, "DELETE FROM docs WHERE file_id = @file", parameters);
        IndexSql.Execute(_database, "DELETE FROM files WHERE id = @file", parameters);
    }

    /// <summary>
    /// Inserts the <c>files</c> row and returns its id.
    /// </summary>
    /// <param name="file">The scanned file.</param>
    /// <param name="content">The file's hash.</param>
    /// <param name="chunks">How many passages the file produced.</param>
    /// <param name="symbols">How many declarations it produced.</param>
    /// <param name="indexedUtc">The timestamp to record.</param>
    /// <returns>The new row id.</returns>
    private long InsertFile(ScannedFile file, IndexFileContent content, int chunks, int symbols, string indexedUtc)
    {
        IndexSql.Execute(
            _database,
            """
            INSERT INTO files (path, language, size_bytes, mtime_utc, sha256, state, skip_reason, chunk_count, symbol_count, indexed_utc)
            VALUES (@path, @language, @size, @mtime, @sha, @state, NULL, @chunks, @symbols, @indexed)
            """,
            IndexSql.Parameters(
                (PathParameter, file.Path),
                ("@language", file.Language),
                ("@size", file.SizeBytes),
                ("@mtime", IndexTimestamps.Format(file.ModifiedUtc)),
                ("@sha", content.Sha256),
                ("@state", IndexFileStates.Ok),
                ("@chunks", (long)chunks),
                ("@symbols", (long)symbols),
                ("@indexed", indexedUtc)));

        return LastInsertedId("SELECT id FROM files WHERE path = @path", IndexSql.Parameters((PathParameter, file.Path)));
    }

    /// <summary>
    /// Inserts every declaration of one file and returns the id each one was stored under.
    /// </summary>
    /// <param name="fileId">The owning file id.</param>
    /// <param name="symbols">The declarations, in file order.</param>
    /// <returns>The row ids, in the same order as <paramref name="symbols"/>.</returns>
    private List<long> InsertSymbols(long fileId, IReadOnlyList<ExtractedSymbol> symbols)
    {
        var ids = new List<long>(symbols.Count);

        foreach (var symbol in symbols)
        {
            IndexSql.Execute(
                _database,
                """
                INSERT INTO symbols (file_id, key, name, qualified_name, kind, namespace, container, start_line, end_line, signature, doc)
                VALUES (@file, @key, @name, @qualified, @kind, @namespace, @container, @start, @end, @signature, @doc)
                """,
                IndexSql.Parameters(
                    (FileParameter, fileId),
                    ("@key", symbol.Key),
                    ("@name", symbol.Name),
                    ("@qualified", symbol.QualifiedName),
                    ("@kind", symbol.Kind),
                    ("@namespace", symbol.Namespace),
                    ("@container", symbol.Container),
                    ("@start", (long)symbol.StartLine),
                    ("@end", (long)symbol.EndLine),
                    ("@signature", symbol.Signature),
                    ("@doc", symbol.Doc)));

            ids.Add(LastInsertedId(
                "SELECT id FROM symbols WHERE file_id = @file AND key = @key",
                IndexSql.Parameters((FileParameter, fileId), ("@key", symbol.Key))));
        }

        return ids;
    }

    /// <summary>
    /// Inserts one file's passages with their vectors.
    /// </summary>
    /// <param name="fileId">The owning file id.</param>
    /// <param name="passages">The passages, in file order.</param>
    /// <param name="cancellationToken">Cancels between passages.</param>
    private void InsertPassages(long fileId, IReadOnlyList<Passage> passages, CancellationToken cancellationToken)
    {
        foreach (var passage in passages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A fresh array per row: the embedder writes into the array it is handed, so a shared buffer would
            // leave every passage of the file holding the vector of the last one.
            var vector = new float[_embedder.Dimensions];
            _embedder.Embed(passage.Text, vector);

            IndexSql.Execute(
                _database,
                """
                INSERT INTO chunks (file_id, start_line, end_line, kind, symbol_key, symbol_name, text, tokens, sha256, embedding)
                VALUES (@file, @start, @end, @kind, @symbolKey, @symbolName, @text, @tokens, @sha, @vector)
                """,
                IndexSql.Parameters(
                    (FileParameter, fileId),
                    ("@start", (long)passage.StartLine),
                    ("@end", (long)passage.EndLine),
                    ("@kind", passage.Kind),
                    ("@symbolKey", passage.SymbolKey),
                    ("@symbolName", passage.SymbolName),
                    ("@text", passage.Text),
                    ("@tokens", CodeTokenizer.TokenizeForIndex(passage.Text)),
                    ("@sha", IndexFileContent.HashText(passage.Text)),
                    ("@vector", vector)));
        }
    }

    /// <summary>
    /// Inserts the documentation passages of one file, attached to the declaration a heading names.
    /// </summary>
    /// <param name="fileId">The owning file id.</param>
    /// <param name="facts">The facts the docs came from.</param>
    private void InsertDocs(long fileId, SyntaxFacts facts)
    {
        if (facts.Docs.Count == 0)
        {
            return;
        }

        var keysByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in facts.Symbols)
        {
            keysByName.TryAdd(symbol.Name, symbol.Key);
        }

        foreach (var doc in facts.Docs)
        {
            var symbolKey = doc.Heading is { Length: > 0 } heading && keysByName.TryGetValue(heading, out var key)
                ? key
                : null;

            IndexSql.Execute(
                _database,
                """
                INSERT INTO docs (file_id, heading, symbol_key, start_line, end_line, text)
                VALUES (@file, @heading, @symbolKey, @start, @end, @text)
                """,
                IndexSql.Parameters(
                    (FileParameter, fileId),
                    ("@heading", doc.Heading),
                    ("@symbolKey", symbolKey),
                    ("@start", (long)doc.StartLine),
                    ("@end", (long)doc.EndLine),
                    ("@text", doc.Text)));
        }
    }

    /// <summary>
    /// Inserts one vector per declaration, so the graph leg can answer a question about a symbol by similarity.
    /// </summary>
    /// <param name="symbolIds">The stored ids, in declaration order.</param>
    /// <param name="symbols">The declarations, in the same order.</param>
    private void InsertSymbolVectors(List<long> symbolIds, IReadOnlyList<ExtractedSymbol> symbols)
    {
        for (var index = 0; index < symbols.Count && index < symbolIds.Count; index++)
        {
            var symbol = symbols[index];
            var vector = new float[_embedder.Dimensions];
            _embedder.Embed(SymbolText(symbol), vector);

            IndexSql.Execute(
                _database,
                "INSERT INTO node_embeddings (node_id, label, embedding) VALUES (@node, @label, @vector)",
                IndexSql.Parameters(
                    ("@node", symbolIds[index]),
                    ("@label", symbol.Key),
                    ("@vector", vector)));
        }
    }

    /// <summary>
    /// Reads the id of a row that was just inserted.
    /// </summary>
    /// <param name="sql">A statement that selects exactly that row's id.</param>
    /// <param name="parameters">The values that identify it.</param>
    /// <returns>The id, or zero when the row is not there.</returns>
    /// <remarks>
    /// A select by the row's own identity rather than <c>GetLastInsertRowId</c>: the engine's counter is shared
    /// by every table on the connection, and one insert that does not update it would silently attach a file's
    /// passages to the previous file.
    /// </remarks>
    private long LastInsertedId(string sql, Dictionary<string, object?> parameters)
    {
        var rows = IndexSql.Query(_database, sql, parameters);
        return rows.Count == 0 ? 0 : IndexSql.AsLong(rows[0].GetValueOrDefault("id"));
    }

    /// <summary>
    /// Builds the text a declaration's vector is computed from.
    /// </summary>
    /// <param name="symbol">The declaration.</param>
    /// <returns>The qualified name followed by the documentation, which is what a question about it shares.</returns>
    private static string SymbolText(ExtractedSymbol symbol) =>
        string.IsNullOrEmpty(symbol.Doc) ? symbol.QualifiedName : symbol.QualifiedName + " " + symbol.Doc;
}
