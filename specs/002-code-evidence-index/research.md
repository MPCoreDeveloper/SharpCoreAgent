# Research: Code Evidence Index (SharpAriadne)

**Feature**: `002-code-evidence-index` | **Date**: 2026-10-24

Every decision below was taken against the actual sources: the SharpCoreDB repository at
`d:\repos\MPCoreDeveloper\SharpCoreDB` (packages `2.1.0-RC.3`, `net11.0`) and the SharpCoreAgent tree at
`d:\repos\MPCoreDeveloper\SharpCoreAgent`. Where a SharpCoreDB behaviour is named, it was read in that
repository, not inferred.

## Verified capabilities this feature builds on

| Capability | Where it lives in SharpCoreDB |
|---|---|
| BM25 full-text index (K1 = 1.2, B = 0.75, phrase boost 1.5) | `SharpCoreDB.Search.Lexical.FullTextIndex` |
| Lexical analyzer (lowercase, length ≥ 2, English stemmer, stop words) | `SharpCoreDB.Search.Lexical.LexicalAnalyzer` |
| Reciprocal-rank fusion (`RrfK` = 60) | `SharpCoreDB.HybridSearch.HybridSearchEngine` |
| HNSW index (`index_type='hnsw'`, `m`, `ef_construction`, `ef_search`) and DiskANN | `SharpCoreDB.VectorSearch.Index.*` |
| SIMD distance `vec_distance_cosine` / `_l2` / `_dot`, `vec_normalize` | `SharpCoreDB.VectorSearch.VectorFunctionProvider` |
| `VECTOR(n)` column type, `float[]` values, `InsertBatch` | `SharpCoreDB.Database` |
| Edge-table traversal over `source`/`target`/`weight` | `SharpCoreDB.Graph.GraphTraversalEngine.EdgeTable` |
| GraphRAG scoring: semantic 0.5 / topological 0.3 / community 0.2 | `SharpCoreDB.Graph.Advanced.GraphRAG.GraphRagEngine.SearchAsync` |
| Load `source`/`target`/`weight` into a graph | `...GraphRAG.SqlIntegration.GraphLoader.LoadFromTableAsync` |
| Louvain communities (30-minute cache) | `...CommunityDetection.LouvainAlgorithm`, `CommunityDetectionFunctions` |
| Degree / betweenness / closeness centrality | `...Metrics.CentralityMetrics` |
| Parameterised SQL (`@name`) and `ExecuteQuery` | `SharpCoreDB.Database`, `DatabaseExtensions` |
| Encryption off for derived data | `DatabaseConfig.NoEncryptMode`, `EnableAtRestRecordEncryption` |

## D1 — Depend on SharpCoreDB packages, not on the project tree

**Decision**: reference `SharpCoreDB`, `SharpCoreDB.Search`, `SharpCoreDB.VectorSearch`,
`SharpCoreDB.Graph`, `SharpCoreDB.Graph.Advanced` and `SharpCoreDB.HybridSearch` at `2.1.0-RC.3` from
the sibling `artifacts` folder, with one MSBuild switch (`SharpCoreAgentUseLocalSharpCoreDb=true`) that
swaps in `ProjectReference`s for engine development.

**Why**: the versions are local pre-releases, so `nuget.org` alone cannot satisfy them, and `artifacts`
is a flat folder of `.nupkg` files — already a valid folder feed. A project-reference-only setup would
drag SharpCoreDB's own build props, analyzers and dozens of projects into every SharpCoreAgent build.

**Cost**: `nuget.config` needs a second `packageSource` plus a `packageSourceMapping` pattern for
`SharpCoreDB*`; `Directory.Packages.props` needs six `PackageVersion` rows. Both are recorded in
`plan.md` under Complexity Tracking, because the constitution requires a stated reason per dependency.

## D2 — One SharpCoreDB database per workspace, unencrypted by default

**Decision**: `<workspace>/.sharpcoreagent/index/sharpcoreagent.scdb`, `DatabaseConfig.NoEncryptMode = true`,
opt-in encryption keyed from `SHARPCOREAGENT_INDEX_KEY`.

**Why**: the index is derived, rebuildable data, and a master-password prompt before every
`sharpcoreagent run` would be unusable. Encryption stays available for a workspace whose *paths* are
themselves sensitive. `.sharpcoreagent` is already in `IgnoredPaths`, so the index never indexes itself.

## D3 — Normalised tables, not one opaque document per file

**Decision**: seven tables (`files`, `chunks`, `symbols`, `edges`, `node_embeddings`, `docs`, `meta`),
exactly as `contracts/index-schema.md` specifies.

**Why**: each retrieval leg needs a different structure — BM25 wants passage text, the vector leg wants
a `VECTOR(n)` column, the graph leg wants `source`/`target`/`weight`. A single JSON blob per file would
force every query through a full scan, and deleting a file would mean rewriting the blob instead of
`DELETE FROM chunks WHERE file_id = ?`.

## D4 — Passages are declaration-aligned, never mid-line

**Decision**: chunk by (a) type declaration, (b) member declaration, (c) blank-line-separated block,
capped at 40 lines / 1 600 characters, split further at line boundaries, no overlap.

**Why**: a passage is consumed by `read_file` next, so a window starting mid-statement produces a
confusing citation and a wasted read. Declaration alignment also lets a passage carry the symbol it
declares, which is what the `kind` filter and the exact-symbol boost need. Overlap was rejected: it
doubles index size for code, where a declaration already repeats the context that matters.

## D5 — Lexical leg: reuse `FullTextIndex`, feed it code-aware text

**Decision**: keep one in-memory `FullTextIndex` per open index, built at open from `chunks.text`, and
insert *pre-split* text — identifiers broken at camelCase/PascalCase/snake_case/dot boundaries and
lowercased, punctuation dropped.

**Why**: `FullTextIndex.Add` runs SharpCoreDB's `LexicalAnalyzer` internally, so a caller cannot plug in
its own analyzer. Its English stemmer is harmless for code — applied identically at index and query
time, so `Repositories`/`repositories` both become `repositori` and still match — but its
"split on non-letters" rule turns `RunTurnAsync` into a single token, unmatched by a query for `turn`.
Pre-splitting is the smallest intervention that fixes the real problem, and the stemmer's collisions
(`parse`/`parsing`) are acceptable in a code corpus.

**Cost**: the BM25 index lives in memory and is rebuilt on open, bounded by `MaxChunks` (default
200 000). Above that bound the lexical leg falls back to a parameterised SQL `LIKE` scan and
`index_status` reports the fallback, rather than silently answering badly.

## D6 — Vector leg: `VECTOR(n)` column, SIMD distance, HNSW above a threshold

**Decision**: store passage and node embeddings as `VECTOR(512)`; query with
`ORDER BY vec_distance_cosine(embedding, @query) LIMIT @k` (SIMD full scan); create the HNSW index
(`m = 16`, `ef_construction = 200`) only when `chunks` exceeds 50 000 rows.

**Why**: a full SIMD scan of 20 000 × 512 floats is single-digit milliseconds and cannot go stale after
an incremental update. HNSW pays off only where the scan dominates; adding it unconditionally buys a
persistence-and-invalidation problem for nothing.

**Cost**: the HNSW index is rebuilt with the database, not maintained per file; at that scale a rebuild
is an explicit `index --rebuild`, which the CLI reports.

**Measured (2026-10-25), and what changed because of it**: the SQL route is not available in
`2.1.0-RC.3`. Four findings, each reproduced against a real index and then traced back to the engine's
source, because "it does not work" is not a decision anyone can build on:

1. `TryExecuteVectorOptimized` in `SqlParser.DML.cs` is a stub that returns `null`, so the
   `ORDER BY vec_distance_*(col, query) LIMIT k` plan never becomes an index scan. It is not a feature
   nobody wrote: `e220461e` added a working ~100-line implementation (argument extractor included), and
   `997103be` — "sync docs distributed featurs", 2026-02-23 — replaced it with the stub and four unused
   parameters. `VectorQueryOptimizer` itself is intact; only the wiring is gone.
2. A projection that computes a function is ignored: `SELECT id, vec_distance_cosine(embedding, @q) AS d
   FROM chunks` returns every column of the row and no `d`. The legacy `ExecuteSelect` path never
   projects a single-table select at all (`ProjectColumns` is called only on the join path), so the
   select list is discarded; the newer AST path does project, through `EvaluateExpression`.
3. `ORDER BY` is handed to `Table.Select` as a column name — `ApplyOrdering` sorts on
   `row.TryGetValue(orderBy)` and its `catch` returns the rows unsorted — so an ordering on a function or
   any other expression is a silent no-op instead of an error.
4. `WHERE vec_distance_cosine(embedding, @q) < 1.1` matches nothing, and `> -1.1` matches nothing either:
   `EvaluateWhere` splits the predicate on spaces and treats `parts[0]` as a column name, so the function
   text never resolves and every row is rejected. An unknown *operator* on a known column, in contrast,
   returns true — the two halves of that evaluator disagree, which is why the empty result reads like a
   data problem rather than a parser one.

Two more defects surfaced on the way and were fixed under T018:

- Binding a `float[]` as a parameter stores the literal text `System.Single[]`; the column's accepted
  textual form is a JSON array (`VectorBinaryCodec.TryParse`), so the writer now sends
  `VectorSerializer.ToJson(vector)` and `analyzer_contract` is 2 to force older indexes to rebuild.
- A `TEXT` literal does not survive `INSERT`: the engine's `ParseInsertValues` trims every literal
  ("3. every literal is Trimmed" in its own comment, pinned by its `InsertValuesParsingTests`), so a
  passage that started with indentation or ended with a blank line was stored trimmed and the stored
  hash no longer matched the stored text — `verify` reported every such passage as corrupt. Passages are
  now hashed over their trimmed text. This is a deviation from SQL rather than a caching artefact:
  `'  a  '` comes back as `a`.

The leg therefore computes cosine over the vectors the snapshot loads, using the same measure the embedder
uses. That keeps the decision (D6) — vectors in the column, one similarity floor, `4 × K` candidates — and
moves the arithmetic one layer up until the engine's optimizer lands.

## D7 — Local hashed embedder by default

**Decision**: `HashedCodeEmbedder`, 512 dimensions: split the text into code tokens, hash each token
with FNV-1a into a dimension, weight by `1 + log(tf)`, L2-normalise.

**Why**: deterministic (so SC-007 and every ranking test are stable), no download, no network, no model
file, and it captures the signal that matters for identifiers, where lexical overlap *is* the semantic
signal.

**Alternative rejected**: calling the configured chat provider's `/embeddings` endpoint by default — it
breaks the offline promise, costs money per file and makes tests non-deterministic. It stays available
as `--embedder provider` behind the same port.

## D8 — Symbol identity is a key string; the row id is internal

**Decision**: `symbols.key = <namespace>.<type>[.<member>]#<kind>`, unique per file, with
`symbols.id` as the graph node id used by `edges` and `node_embeddings`. A rebuild assigns new row ids
but the same keys.

**Why**: edges, communities and evidence need a stable name, not a row number that changes when a file
earlier in the scan is added. It also makes `find_symbol` exact-matching cheap (`WHERE key = @key`)
before falling back to a name match.

**Cost**: a symbol moved between files changes its key only by file, so it is re-keyed and re-linked —
which is correct, because its edges belong to the file that declares it.

## D9 — Edge weights compress call sites; no row per occurrence

**Decision**: one edge per `(source, target, rel)` with `weight = min(occurrences, 5)`; `inherits`,
`implements`, `imports` and `declares` use weight 1; `doc_mentions` uses 1 `+` heading depth.

**Why**: a hot helper has hundreds of call sites and every one of them would be a row the graph engines
load per query. The retrieval question is "how strongly related", which a weight answers; the exact
call-site *lines* are answered from `chunks` text when needed, not from the edge table. This is the same
trade-off AIRK makes by omitting routine call occurrences from its lean profile — but we keep the
relationship, which is what the graph is for.

## D10 — Communities and centrality at build time, traversal at query time

**Decision**: run Louvain once per build and store `symbols.community`; optionally store
`symbols.centrality` (degree). At query time, traverse an in-memory adjacency snapshot built at open
(forward and reverse maps) with a weighted BFS bounded by `hops`.

**Why**: `GraphRagEngine.SearchAsync` loads the whole edge table per call
(`GraphLoader.LoadFromTableAsync`), which is right for an ad-hoc SQL query and wrong for a tool a model
may call ten times per turn. Precomputing the expensive part (communities) and keeping the cheap part
(BFS over a snapshot) in memory gives the same 0.5 / 0.3 / 0.2 shape as the engine without the reload.

**Rejected**: caching `GraphRagEngine` results per query string — it hides staleness and produces
answers that are right for the previous build.

## D11 — Fusion, boosts and the output budget

**Decision**: run the legs, keep each leg's ranked list, fuse with RRF (`k = 60`, same constant the
hybrid engine uses), then multiply by bounded boosts:

| Signal | Multiplier |
|---|---|
| query token equals a declared symbol name exactly | ×1.6 |
| passage declares a type (not a member) | ×1.15 |
| path matches a token of the query | ×1.10 |
| `docs` row mentions the symbol | ×1.05 |
| file modified in the last 10 minutes | ×1.02 |
| file's `files.state != 'ok'` | ×0.50 |

then apply the graph term for the surviving seeds (`0.5 × semantic + 0.3 × topology + 0.2 × community`,
as `GraphRagEngine` does), and finally the selection budget: ≤ 12 blocks, ≤ 40 lines and ≤ 1 600
characters per block, ≤ 2 blocks per file, whole result under `MaxToolResultChars`.

**Why RRF**: it needs no score calibration between BM25 (unbounded) and cosine (0–1), it is what the
hybrid engine already does, and it is stable under one leg returning nothing — which is the common case
when `--embedder off`.

**Why a per-file cap**: five passages from the same 300-line file tell the model less than one from each
of five files, and it silently spends the whole budget on the file that happens to repeat the query
term. The cap is what makes the token saving in SC-004 real.

## D12 — Writer lock, per-file transactions, crash safety

**Decision**: `<index>/.lock` opened exclusively for the writer; each file is written in one
transaction (`DELETE` its chunks/symbols/edges/vectors, then insert, then `UPDATE files`); the build id
and timestamp in `meta` are updated last. A reader opens read-only and treats a missing or mismatched
`meta.build_id` as "stale".

**Why**: the fail mode without a lock is two processes interleaving deletes and inserts, which produces
a database that looks fine and answers wrongly. Per-file transactions mean a crash costs one file's
freshness, not the whole index. Ordering the metadata last means a half-written build is *detected*
rather than trusted.

## D13 — Determinism

**Decision**: no `Dictionary` iteration in ranking, no `DateTime.Now` in scores, stable ordering by
`(score desc, path asc, start_line asc)`, file enumeration sorted ordinally as `SearchTextTool` does,
float scores rounded to 6 decimals before comparison.

**Why**: without this, two runs disagree and every ranking test becomes flaky. It is also what lets the
agent trust "the first block" as a fact rather than a sample.

## D14 — Alternatives considered and rejected

| Alternative | Why not |
|---|---|
| SQLite + `fts5` + a hand-rolled graph | a second storage engine next to the one this repository already ships, and no graph/community support |
| Lucene.NET | large dependency, no graph, no native vectors at this size |
| Index every file, no size limit | generated files dominate the index and the token budget, and `search_text` already refuses them |
| Store whole files and embed them | retrieval granularity too coarse to cite, and one vector per file loses the passage |
| Rebuild on every open | turns a 3-second start into a 90-second start; incremental is the whole point of FR-009 |
| MCP server exposing the index | out of scope for v1: it adds a process boundary and a protocol for something the tools call in-process |
| Roslyn extractor now | `Microsoft.CodeAnalysis` is a large dependency (Principle: reason per dependency) and it only helps C#; `ISyntaxFactsExtractor` is the seam that lets it arrive later |

## D15 — Risks

- **Pre-release dependency**: `2.1.0-RC.3` is not on nuget.org. Mitigation: folder feed in
  `nuget.config`, the `SharpCoreAgentUseLocalSharpCoreDb` switch, and a build-time error if neither resolves.
- **Memory**: BM25 + adjacency snapshot + vectors are in memory. Mitigation: `MaxChunks` bound, passage
  vectors read per query rather than held, and `index_status` reporting counts so the developer can see
  the size before it hurts.
- **Extraction quality is heuristic**: a wrong `calls` edge produces a wrong neighbour. Mitigation: edges
  are ranked, never asserted; every block still carries a path and line range to verify.
- **Index write on a large first run**: the first `sharpcoreagent run` in a 5 000-file repository has work to
  do. Mitigation: the turn-start budget (FR-021) plus `partial` coverage, and `sharpcoreagent index` for the
  developer who wants it done up front.


