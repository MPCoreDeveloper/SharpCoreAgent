# Contract: Index Schema

**Scope**: the SharpCoreDB database inside `<workspace>/.sharpcoreagent/index/`. Only `SharpCoreAgent.Indexing`
may name these tables or columns; everything else goes through `ICodeIndex` / `ICodeIndexMaintainer`.

**Engine**: SharpCoreDB `2.1.1`. The SQL used here is the subset verified against the shipped package:
`CREATE TABLE IF NOT EXISTS`, `INTEGER PRIMARY KEY AUTO` (monotonic, persisted), `TEXT`, `REAL`,
`INTEGER`, `VECTOR(n)`, `CHECK (…)`, `COLLATE NOCASE`, `CREATE INDEX … ON t (col)` and parameterised
`@name` arguments, executed through `IDatabase.ExecuteSQL`, `IDatabase.ExecuteQuery` and
`IDatabase.ExecuteBatchSQL`.

Four facts about that package the code has to live with, all learned from `IndexSchemaTests`:

1. There is no `ExecuteNonQuery` and no `ExecuteScalar`. Statements that return nothing go through
   `ExecuteSQL`, and a value is read with `ExecuteQuery` and a one-row result.
2. `ExecuteQuery` does not carry a column alias: `SELECT COUNT(*) AS n` comes back under a different key.
   Every read names its columns and maps by column name, never by position or alias.
3. `DatabaseFactory.CreateWithOptions` rejects a blank master password even when
   `DatabaseOptions.EnableEncryption` is `false`, so the unencrypted index passes a constant placeholder.
4. `IDatabase` is `IAsyncDisposable` since 2.1.1, so closing the index is `Database.DisposeAsync()`; the
   cast-to-`IDisposable` fallback the 2.1.0 adapter needed is gone.
5. The factory appends `.scdb` to any path that does not already end with it, and preallocates the file
   (≈14 MiB for an empty schema), so the index path carries the extension and a fresh workspace reports a
   size before it holds a single row.
   (≈14 MiB for an empty schema), so the index path carries the extension and a fresh workspace reports a
   size before it holds a single row.

**Files** (all under `.sharpcoreagent/index/`, nothing may exist outside it):

| File | Purpose |
|---|---|
| `sharpcoreagent.scdb` | the database |
| `sharpcoreagent.scdb.wal` | engine journal, if the engine writes one |
| `.lock` | zero-byte writer lock, opened with `FileShare.None` |

## DDL (executed in this order, in one `ExecuteBatchSQL` batch, idempotent)

```sql
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

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
);
CREATE INDEX IF NOT EXISTS idx_files_path ON files (path);
CREATE INDEX IF NOT EXISTS idx_files_sha  ON files (sha256);

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
    embedding   VECTOR(512)
);
CREATE INDEX IF NOT EXISTS idx_chunks_file   ON chunks (file_id);
CREATE INDEX IF NOT EXISTS idx_chunks_symbol ON chunks (symbol_key);

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
);
CREATE INDEX IF NOT EXISTS idx_symbols_key       ON symbols (key);
CREATE INDEX IF NOT EXISTS idx_symbols_name      ON symbols (name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_symbols_file      ON symbols (file_id);
CREATE INDEX IF NOT EXISTS idx_symbols_community ON symbols (community);

CREATE TABLE IF NOT EXISTS edges (
    id      INTEGER PRIMARY KEY AUTO,
    source  INTEGER NOT NULL,
    target  INTEGER NOT NULL,
    rel     TEXT NOT NULL CHECK (rel IN ('calls','references','inherits','implements','imports','declares','doc_mentions')),
    weight  REAL NOT NULL,
    file_id INTEGER
);
CREATE INDEX IF NOT EXISTS idx_edges_source ON edges (source);
CREATE INDEX IF NOT EXISTS idx_edges_target ON edges (target);

CREATE TABLE IF NOT EXISTS node_embeddings (
    id        INTEGER PRIMARY KEY AUTO,
    node_id   INTEGER NOT NULL,
    label     TEXT NOT NULL,
    embedding VECTOR(512)
);
CREATE INDEX IF NOT EXISTS idx_node_embeddings_node ON node_embeddings (node_id);

CREATE TABLE IF NOT EXISTS docs (
    id         INTEGER PRIMARY KEY AUTO,
    file_id    INTEGER,
    heading    TEXT,
    symbol_key TEXT,
    start_line INTEGER NOT NULL,
    end_line   INTEGER NOT NULL,
    text       TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_docs_symbol ON docs (symbol_key);
```

**Uniqueness** is enforced by the writer, not by the engine: each file's rows are deleted and re-inserted
in one transaction, and the scanner visits each real path once. A `UNIQUE` index is not assumed because
`UNIQUE INDEX` support is not part of the verified subset; `VerifyAsync` reports duplicates instead.

**`edges.source`/`edges.target`/`edges.weight`** are those exact names because
`GraphLoader.LoadFromTableAsync` and `GraphTraversalEngine.EdgeTable` read them by name. Renaming one of
them silently turns the graph leg off, so the schema test asserts the three column names.

**`edges` are symbol ids.** A `called_by` answer is produced by reading `edges` in the reverse direction,
not by writing a mirrored row: the writer always stores the relation from the caller to the callee, and
the reader reverses it.

**`chunks.tokens`** is the pre-split, lowercased, space-separated form the BM25 index is built from
(research D5). It is stored rather than recomputed so that rebuilding the in-memory lexical index after a
restart is a read, not a tokenize pass over every passage.

**`node_embeddings`** holds vectors for symbol nodes only; passage vectors live on the chunk row, so the
vector leg is one query and not a join.

## `meta` keys

Every key is written on every build. `build_id` and `build_utc` are written **last**, inside the final
transaction, so a crash leaves the previous values and `VerifyAsync` can detect an interrupted build.

| Key | Example | Meaning |
|---|---|---|
| `schema_version` | `1` | this document's version; a mismatch ⇒ rebuild, never a migration in v1 |
| `parser_version` | `1` | heuristic extractor version; a mismatch ⇒ re-derive every file's symbols |
| `analyzer_contract` | `3` | chunking + tokenisation + stored-vector shape; a mismatch ⇒ re-chunk every file |
| `tier` | `lean` | `lean` \| `rich` |
| `embedder` | `hashed-local/512` | identity of the embedder, including dimensions |
| `dimensions` | `512` | must equal the `VECTOR(n)` declarations |
| `quantization` | `int8` | how vectors are stored |
| `workspace_root` | `d:/repos/SharpCoreAgent` | absolute, forward slashes; a mismatch ⇒ this index belongs elsewhere, rebuild |
| `build_id` | `01J9Z8…` | ULID of the last complete build |
| `build_utc` | `2026-10-24T09:12:44Z` | ISO-8601 UTC of the last complete build |
| `build_state` | `ok` | `ok` \| `partial`; `partial` means the budget cut the build short |
| `file_count` / `chunk_count` / `symbol_count` / `edge_count` | `412` | counters for `index --status` without a table scan |

## Invariants (asserted by schema tests and by `index --verify`)

1. `chunks.end_line >= chunks.start_line` and `end_line - start_line + 1 <= 40`.
2. `chunks.text` is non-empty and `length(chunks.text) <= 1600`.
3. `chunks.sha256` is 64 lowercase hex characters and equals SHA-256 of `chunks.text`.
4. Every `chunks.file_id`, `symbols.file_id`, `edges.file_id` and `docs.file_id` names an existing
   `files.id`.
5. Every `edges.source` and `edges.target` names an existing `symbols.id`; no self-edge.
6. `(edges.source, edges.target, edges.rel)` appears at most once.
7. Every `symbols.key` is unique within its file and non-blank; `symbols.end_line >= symbols.start_line`.
8. `files.path` is workspace-relative, uses `/`, never starts with `/`, never contains `..`, never
   contains `\`.
9. `files.state = 'ok'` implies `chunk_count >= 1`; `files.state = 'skipped'` implies `skip_reason IS NOT NULL`.
10. `meta['dimensions']` equals the dimension in the `VECTOR(n)` declarations, and every stored vector
    has exactly that length.
11. No row contains the workspace root as an absolute path prefix.

## Query shapes (the only statements the adapter runs)

```sql
-- status
SELECT value FROM meta WHERE key = @key;
SELECT COUNT(*) FROM files WHERE state <> 'ok';

-- staleness, given the scanner's (path, size_bytes, mtime_utc) triples
SELECT path, size_bytes, mtime_utc, sha256, state FROM files;

-- write path (one file, one transaction, in this order)
DELETE FROM node_embeddings WHERE node_id IN (SELECT id FROM symbols WHERE file_id = @fid);
DELETE FROM edges    WHERE file_id = @fid;
DELETE FROM chunks   WHERE file_id = @fid;
DELETE FROM symbols  WHERE file_id = @fid;
DELETE FROM docs     WHERE file_id = @fid;
DELETE FROM files    WHERE id = @fid;
-- then INSERTs, then:
UPDATE meta SET value = @v WHERE key = 'build_id';  -- and build_utc, build_state, counters

-- vector leg (passage level, SIMD scan)
SELECT id, file_id, start_line, end_line, symbol_name, kind, text, sha256, tokens,
       vec_distance_cosine(embedding, @q) AS distance
FROM chunks
WHERE @path_prefix IS NULL OR file_id IN (SELECT id FROM files WHERE path LIKE @path_prefix)
ORDER BY distance ASC
LIMIT @k;

-- exact/infix symbol lookup
SELECT * FROM symbols WHERE key = @key;
SELECT * FROM symbols WHERE name = @name COLLATE NOCASE LIMIT @k;
SELECT * FROM symbols WHERE name LIKE @infix COLLATE NOCASE LIMIT @k;

-- graph edges of a symbol, both directions
SELECT source AS other, rel, weight FROM edges WHERE target = @id
UNION ALL
SELECT target AS other, rel, weight FROM edges WHERE source = @id;
```

**The vector-leg statement is the target shape, not what runs today.** `2.1.1` carries the repaired planner
(`16cb51d5`), but it engages only on a *directory-mode* database: on this index's single `.scdb` file the
`ORDER BY vec_distance_cosine(…)` statement still returns the rows in insertion order and projects no
`distance` column. `VectorRetriever` therefore computes the same cosine in C# over the vectors the snapshot
loaded; the statement above is what the leg moves to when the planner covers the single-file route, and the
schema does not change when it does.

Every value above is a bound parameter. No query string is ever built by concatenation — the
`LIKE` pattern is `"%" + escaped + "%"`, assembled in C# and passed as `@path_prefix`/`@infix`, so the
statement text itself stays constant (Principle III).

## Migration rule

`schema_version` is compared on open:

- **equal** → open normally;
- **lower** → rebuild the index (v1 has no migration path, by design: it is derived data);
- **higher** → refuse to open, report `Corrupt` with "index written by a newer SharpAriadne", and let the
  maintainer rebuild only when the caller explicitly asks (`index --rebuild`).

The same rule applies to `analyzer_contract` (re-chunk) and `parser_version` (re-extract facts), except
those can be repaired in place without recreating tables: a build that finds either of them different
empties the six data tables before it walks the workspace, because an incremental pass would otherwise
keep every file whose hash still matches and the rows would stay derived by the old rules for ever. The
three version keys are rewritten with the rest of `meta` at the end of that build, so the index reports
itself fresh afterwards instead of stale against the values the first open seeded.


