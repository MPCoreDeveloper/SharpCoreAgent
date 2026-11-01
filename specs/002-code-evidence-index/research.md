# Research: Code Evidence Index (SharpAriadne)

**Feature**: `002-code-evidence-index` | **Date**: 2026-10-24

Every decision below was taken against the actual sources: the SharpCoreDB repository at
`d:\repos\MPCoreDeveloper\SharpCoreDB` (packages `2.1.2`, `net11.0`) and the SharpCoreAgent tree at
`d:\repos\MPCoreDeveloper\SharpCoreAgent`. Where a SharpCoreDB behaviour is named, it was read in that
repository, not inferred. Version-qualified findings name the release they were measured on, because the
engine moved three times during this feature (`2.1.0-RC.3` → `2.1.0` → `2.1.1` → `2.1.2`) and a finding
without its release stops being true without anyone noticing.

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
`SharpCoreDB.Graph`, `SharpCoreDB.Graph.Advanced` and `SharpCoreDB.HybridSearch` at `2.1.2` from
nuget.org, with one MSBuild switch (`SharpCoreAgentUseLocalSharpCoreDb=true`) that
swaps in `ProjectReference`s for engine development.

**Why**: a project-reference-only setup would drag SharpCoreDB's own build props, analyzers and dozens of
projects into every SharpCoreAgent build, and the graph + BM25 + vector stack is not something this
repository should own. The packages are published on nuget.org — one feed, one version — so a clone
restores them like any other dependency; the RC-era local folder feed was retired when `2.1.0` shipped.

**Cost**: six `PackageVersion` rows in `Directory.Packages.props`, and a version that is a floor rather
than a preference (next paragraph). Both are recorded in `plan.md` under Complexity Tracking, because the
constitution requires a stated reason per dependency.

**Measured (2026-10-26): the floor is `2.1.2`, and falling back to `2.1.1` is not free.** `2.1.1` rendered
a bound value into the statement text and then let a *second*, differently-escaped scanner read it as SQL,
so a `TEXT` holding an apostrophe, a doubled quote or a backslash shifted a column left or failed the parse
with *Mismatched parentheses in VALUES clause*. Reproduced on the shipped packages through
`ProseRoundTripTests`, which holds the shapes SharpCoreDB's own known-issue 7 reproduction names — `it's`
mid-tuple, a stray backslash-quote, a value ending in a backslash, and the parentheses-and-commas control —
plus a doubled-quote run (`'quoted', 'doubled''quotes', 1, 2, 3`), a Windows path holding two apostrophes,
and a workspace path with an apostrophe in it: six of the eight cases fail on `2.1.1` — `it's` came back as
`its`, `'quoted', 'doubled''quotes', 1, 2, 3` as `quoted`, the stray-quote and trailing-backslash shapes
threw, and the indented passage lost its surrounding spaces — and all eight pass on `2.1.2`, where every
literal goes through one `Services/SqlLiteralScanner.cs` (known issue 7). Parentheses and commas alone were
always safe, which is why the defect could hide: the cases that broke needed a quote or a backslash, and most
code has neither. The whitespace case is the one that also explains an existing line of ours: `PassageChunker`
trims a passage at its edges because `2.1.1` stripped that whitespace anyway, and `2.1.2` keeps it, so the
trim is now the chunker's choice instead of a workaround. The exposure was on the read side and in the
statements that carry a path or a query string; passages were already written through the table API, which
never renders a row as text. `Directory.Packages.props` therefore pins `2.1.2` and the test holds those
shapes, so a fall back fails the suite instead of quietly storing prose minus its apostrophes.

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

**Measured (2026-10-25), and what changed because of it**: the SQL route is not available in `2.1.0`, the
release that was consumed when this was first measured (and not in the `2.1.0-RC.3` before it). Four
findings, each reproduced against a real index and then traced back to the engine's source, because "it
does not work" is not a decision anyone can build on:

1. `TryExecuteVectorOptimized` in `SqlParser.DML.cs` is a stub that returns `null`, so the
   `ORDER BY vec_distance_*(col, query) LIMIT k` plan never becomes an index scan. It is not a feature
   nobody wrote: `e220461e` added a working ~100-line implementation (argument extractor included), and
   `997103be` — "sync docs distributed featurs", 2026-02-23 — replaced it with the stub and four unused
   parameters. `VectorQueryOptimizer` itself is intact; only the wiring is gone.
   There was a second, independent break behind the same symptom: `SqlParser.VectorQueryOptimizer` was an
   `internal static` that no code in the engine ever assigned, and no other assembly *could* assign (the
   parser is `internal`), while `SharpCoreDB.VectorSearch` only registers the optimizer with the DI
   container. Both consumers were null-conditional, so `CREATE VECTOR INDEX` built nothing either — the
   whole SQL surface was dead, not just the planner branch. **Repaired upstream** on branch
   `fix/vector-sql-optimizer` (commit `16cb51d5`): `Database.GetSharedSqlParser` now resolves
   `IVectorQueryOptimizer` from the service provider and attaches it per parser instance, and the
   restored method projects onto the SELECT list and refuses `DESC`/`WHERE` (which an ascending top-k
   cannot answer) instead of returning the wrong shape. `2.1.1` ships that repair — and the C# leg still
   stays, for a reason that only showed up when the repair was measured (see the re-measurement below).
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

Two more defects surfaced on the way and were fixed under T018, and a third appeared when the dependency
moved from the RC to the 2.1.0 release:

- Binding a `float[]` as a parameter stores the literal text `System.Single[]` — not a similarity-score
  problem but a lost value. The column's accepted textual form is a JSON array. Fixed upstream in
  `32227696` (2026-10-11), which gave the format and the coercion a single definition
  (`VectorBinaryCodec`) and routed both parameter binders through it. `2.1.0` (2026-10-10) predates that
  fix, so the writer encoded the array itself (`VectorSerializer.ToJson`) and `analyzer_contract` went to
  2 to have those cells rewritten. `2.1.1` carries it: the writer binds the embedder's `float[]` again,
  `EncodeVector` is gone, and `analyzer_contract` is 3 — the same argument one step further, because the
  cells those older indexes hold are JSON text and the rows written now are the engine's own binary form.
- A `TEXT` literal does not survive `INSERT`: the engine's `ParseInsertValues` trims every literal
  ("3. every literal is Trimmed" in its own comment, pinned by its `InsertValuesParsingTests`), so a
  passage that started with indentation or ended with a blank line was stored trimmed and the stored
  hash no longer matched the stored text — `verify` reported every such passage as corrupt. Passages are
  now hashed over their trimmed text. This is a deviation from SQL rather than a caching artefact:
  `'  a  '` comes back as `a`.
- **`2.1.0` changed the shape a `VECTOR(n)` read hands back.** RC.3 returned the stored JSON text
  unchanged; 2.1.0 materializes the row through `OptimizedRowParser` and its AOT JSON path, so the same
  column arrives as a `System.Text.Json.JsonElement`. A reader that accepted only text and `float[]` — as
  `IndexReader.AsVector` did — then saw no vector at all: the index built cleanly, `meta.dimensions`
  matched the embedder, and every passage was skipped as having "no direction to compare", which is
  exactly the silent empty answer the floor and the note were designed to make visible. Three
  `VectorRetrieverTests` failed on the bump this way, and `AsVector` now also accepts the element, routing
  its raw JSON through the same parser as the text form.

**Re-measured against `2.1.1` (2026-10-25)**: the release closes both defects — a bound `float[]` reaches a
`VECTOR(n)` column as a real vector, and a `VECTOR(n)` read on this index's route hands back a `float[]`.
It does not close the SQL route for this index. The repaired planner engages only on a *directory-mode*
database, and `<index>/sharpcoreagent.scdb` is a single file: on that route `ORDER BY
vec_distance_cosine(embedding, ?) LIMIT k` still returns the rows in insertion order and projects no
`distance` column, and `WHERE vec_distance_cosine(…)` still matches nothing. The probe tells the two cases
apart by the presence of the projected `distance` column, because `information_schema.indexes` returns rows
either way. The leg therefore keeps computing cosine over the vectors the snapshot loads, using the same
measure the embedder uses: the decision (D6) — vectors in the column, one similarity floor, `4 × K`
candidates — holds, with the arithmetic one layer up. When the planner covers the single-file route the
same `ORDER BY vec_distance_cosine(embedding, @q) LIMIT 4 * K` moves into SQL without changing the schema
or the floor.

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

- **Engine defects that postdate the release**: `2.1.2` closes the three that mattered — `32227696` (a bound
  `float[]` now reaches a `VECTOR(n)` column), `16cb51d5` (the SQL vector optimizer is wired again) and
  known issue 7 (one literal scanner, so a bound `TEXT` holding an apostrophe, a doubled quote or a
  backslash round-trips instead of shifting a column; `ProseRoundTripTests`, D1). The residual gap is
  narrower and measured: the planner engages only on a directory-mode database, so this single-file index
  still ranks in C# (D6). Mitigation for anything the next release breaks is the
  `SharpCoreAgentUseLocalSharpCoreDb` switch, which builds against a sibling checkout when a newer engine has
  to be tried before it ships. The version is the released `2.1.2` on nuget.org — the RC-era folder feed this
  section used to describe is gone, and `nuget.config` names one source.
- **Memory**: BM25 + adjacency snapshot + vectors are in memory. Mitigation: `MaxChunks` bound, passage
  vectors read per query rather than held, and `index_status` reporting counts so the developer can see
  the size before it hurts.
- **Extraction quality is heuristic**: a wrong `calls` edge produces a wrong neighbour. Mitigation: edges
  are ranked, never asserted; every block still carries a path and line range to verify.
- **Index write on a large first run**: the first `sharpcoreagent run` in a 5 000-file repository has work to
  do. Mitigation: the turn-start budget (FR-021) plus `partial` coverage, and `sharpcoreagent index` for the
  developer who wants it done up front.

## D16 — Measured build cost and index size (2026-10-27), and what SC-002 and SC-006 can claim

**Measured**: the shipped CLI on this repository, .NET 11 Release, one developer machine. Three cold builds
of nested corpora (each into its own `.sharpcoreagent`), then a no-change rebuild of the whole repository:

| Corpus | Files | Passages | Symbols | Edges | Communities | Cold build | Index |
|---|---|---|---|---|---|---|---|
| `src\SharpCoreAgent.Application` | 7 | 84 | 73 | 34 | 10 | 0.7 s | 6.1 MiB |
| `src\SharpCoreAgent.Infrastructure` | 35 | 476 | 419 | 230 | 54 | 5.9 s | 9.1 MiB |
| this repository | 459 | 10 314 | 4 571 | 5 284 | 329 | 513.5 s | 69.2 MiB |
| this repository, second run | 459, 0 changed | — | — | — | — | 4.0 s | — |

**Two success criteria are not met, and the gap is not a rounding error:**

- **SC-002** asks for a cold index of 5 000 files / 50 MB in under 90 s. This repository is a ninth of that
  corpus — 459 files, 14.28 MiB of source once `bin`, `obj`, `.git`, `.sonarqube` and `.sharpcoreagent` are
  excluded — and the build takes 513.5 s, 5.7× the entire budget on 9% of the work. The cost per passage
  *rises* with the size of the database: 8.3 ms, then 12.4 ms, then 49.8 ms across the three rows, a power
  law with an exponent near 1.4. Extrapolating that to 5 000 files at this passage density (22.5 per file, so
  roughly 112 000 passages) gives hours, not a minute and a half. The build is therefore not "90 s on a
  laptop with a slower disk"; it is superlinear in passages and nowhere near the criterion at any size
  measured.
- **SC-006** asks for the index directory to stay under 50% of the source tree. It is 4.8× (69.2 MiB against
  14.28 MiB). A fixed floor is part of that — a 7-file index is already 6.1 MiB, so the file carries
  megabytes before the first passage — but the floor cannot explain the ratio, because the 35-file corpus is
  already 9.1 MiB against roughly 1 MiB of source.
- The incremental half of SC-002 is a near miss rather than a miss: a no-change rebuild is 4.0 s against
  "under 3 s with one changed file". It is, however, above the 3 s turn budget (FR-021), which is why that
  budget is a bound that ends in `partial` coverage rather than a promise of freshness.
- **SC-003** (p95 < 250 ms for `search_code` on a 20 000-passage index) is **not measured**. This index holds
  10 314 passages, half the size the criterion names, and there is no benchmark harness; the retrieval tests
  assert ranking, not latency. It is listed as open rather than assumed.

**Decision: the criteria stay as written and the measurement sits next to them.** Rewriting SC-002 to "900 s
for 5 000 files" would turn a performance defect into a specification, and the four numbers above are exactly
what the next engine release has to be measured against. `spec.md` therefore carries a measured note on
SC-002, SC-003 and SC-006 pointing here, and `tasks.md` T034 is the open work. No task in `tasks.md` ever
claimed otherwise: T022 records the 200-file/25-query quality benchmark as a manual run, and `plan.md` calls
the 5 000-file performance smoke "not in CI" — the gap was that nobody had run it on a corpus large enough
for the difference to show, and the quickstart's 100-line example corpus is far too small to reveal it.

**What would move it, in the order worth trying.** All four phases sit inside the timed window — extraction,
the per-file transaction that writes the rows, the vector encode, and the community assignment over the whole
graph at build time (D10) — so attribution comes before a fix. The first experiment is batching the writes:
the rows are written one statement at a time today, and `InsertBatch` is in the engine's surface (see the
capability table above). The second is moving community assignment out of the per-file path or bounding it.
An engine-level fix is tried through D1's `SharpCoreAgentUseLocalSharpCoreDb=true` switch against a sibling
checkout before it ships, which is what that switch exists for.

## D17 — Measured again (2026-11-01): twenty-five percent more passages, fifty-nine times the clock

**Measured**: `sharpcoreagent index` on this repository, .NET 11 Release, the machine D16 used. The database it
started from was the 233.4 MiB one earlier rounds had left behind; the duration is the run's own output and the
two timestamps on its log file, which agree; the size is the `.scdb` file afterwards.

| | D16 (2026-10-27) | this run (2026-11-01) |
|---|---|---|
| Files | 459 | 666 indexed (652 scanned: 590 added, 62 unchanged, 14 skipped) |
| Passages | 10 314 | 12 889 |
| Symbols · edges · communities | 4 571 · 5 284 · 329 | 6 268 · 8 227 · 582 |
| Wall clock | 513.5 s | **30 345 s** (8 h 20 m) |
| CPU time | not recorded | 865 s (14 min) |
| Database | 69.2 MiB | **350.2 MiB** (367 165 440 bytes) |
| Source, `git ls-files` | 14.28 MiB claimed | 5.98 MB over 519 files |
| Index ÷ source | 4.8× | **61×** |
| Per passage | 49.8 ms | **2 354 ms** |

**Four things it says, in the order they matter:**

1. **Twenty-five percent more passages cost fifty-nine times the clock.** D16 fitted a power law with an
   exponent near 1.4 and said the build was not "90 s on a laptop with a slower disk"; this is what that curve
   looks like once the database underneath it has grown to 350 MiB. The run spent 97% of its wall clock
   waiting and 3% on the CPU, which locates the cost in the engine's per-file transaction against a single
   file (D12) rather than in extraction, the vector encode or the community assignment — those would burn CPU.
   *(Both the CPU figure and the "waiting" reading are withdrawn in D18: the number came from sampling the
   wrapper process. The conclusion survives a proper measurement — the cost is in touching the store, not in
   the file — but the evidence for it is D18's, not this paragraph's.)*
2. **Nothing reclaims space.** `AutoVacuum` is off on the one place a handle is opened, and no code path
   vacuums a database that is only ever appended to and deleted from, so the file keeps whatever earlier builds
   left in it. That is how a 6 MB tree produces a 350.2 MiB index: 61× the source where SC-006 asks for 0.5×,
   against 4.8× when D16 measured it. *(D18 measured the other half of this: a later run that rewrote 43 files
   left the file byte-identical, so "every build adds to it" was too strong — and it still never comes down.)*
3. **The command's own budget was not honoured.** `IndexCommand.CommandBudget` is one hour; this run took
   8 h 20 m, so the budget is either not consulted on this path or consulted at a granularity coarser than one
   file. The budget is also what sets `build_state`, and the index reports `partial` — so the command tells the
   developer to "run `sharpcoreagent index` to finish" on an index whose walk completed (590 added plus 62
   unchanged is the 652 it scanned, and no "budget reached" note was printed). Whether the flag is wrong or the
   budget is wrong is unresolved; both cannot be right, and the flag is the one a developer acts on.
4. **D16's source size does not reproduce.** `git ls-files` sums 5.98 MB for the 519 tracked files of this tree
   today, and this tree is larger than the one D16 measured, not smaller. Either D16 summed a different set or
   one of the two figures is wrong. Nothing SC-006 concludes depends on it: 4.8× and 61× both miss 0.5×, and
   the fixed-floor argument D16 makes is untouched by either.

**Decision: the criteria stay, and the order of the fix is now measured rather than guessed.** D16 put batching
the row writes first (`InsertBatch` is in the engine's surface, and nothing in the index uses it yet); this run
adds the second step and the reason for its position — a build that batches its inserts into a 350 MiB file
still pays for 350 MiB of the file on every transaction, so reclaiming space is not cosmetic work that can wait
behind the batching. Then the same three numbers are measured again: passages, wall clock, bytes.

## D18 — Same day, properly instrumented: a writing build costs its own index, not its own files

**D17's first point is wrong and this entry retires it.** The "97% waiting, 3% CPU" figure came from sampling
the process the shell tool started rather than the indexer it spawned; measured on the child
(`Process.TotalProcessorTime`), the same work is CPU-bound. Everything else in D17 stands — the durations, the
bytes, the 61×, the budget and `partial` observations — but the mechanism it inferred does not.

**Measured**, five runs on this repository plus two controls in a scratch workspace of ten `.cs` files copied out
of `SharpCoreAgent.Domain.Index`, all with the release binary and the same 350.2 MiB index unless stated:

| Run | Wall clock | CPU | What it wrote |
|---|---|---|---|
| 704 files, nothing changed | 5.5 s | 6.0 s | nothing |
| `specs/002-code-evidence-index/tasks.md` touched | 123.7 s | 124.9 s | 1 changed |
| `README.md` touched | 119.3 s | 120.5 s | 1 changed |
| `src/SharpCoreAgent.Cli/Commands/IndexCommand.cs` touched | 131.7 s | 132.6 s | 1 changed |
| scratch workspace, first build (114 passages) | 0.9 s | 1.0 s | 10 added |
| scratch workspace, one file touched | 0.7 s | 0.7 s | 1 changed |

**Three facts, and one that does not fit:**

1. **Walking and opening are not the cost.** A build that changes nothing walks 704 files, opens the store,
   counts four tables, compares hashes and prints a report in 5.5 s. Whatever the 8 h 20 m was, it was not the
   scan.
2. **Writing anything costs about 120 s, and it does not matter what.** A markdown specification, the README and
   a C# file land within 12 s of each other, so the price is not the file's content, its size or its language.
   It is the store being touched.
3. **That price grows with the store, superlinearly.** The same operation — one changed file — costs 0.7 s where
   114 passages are stored and 120 s where 13 575 are: 119× the rows for roughly 1 000× the time. That is D16's
   fitted exponent (≈1.5) seen from the other end, and it is the whole explanation of the build duration: a build
   from scratch touches the store once per file, so its cost is superlinear in the corpus *and* linear in the
   files, which is why 590 files became 8 h 20 m where the same 590 files cost minutes when the store already
   holds them.
4. **The one run that does not fit.** The 43-file run in the warm-up to this measurement (38 added, 5 changed)
   took 222 s. Forty-three writes at the rate the single-file runs set is 86 minutes, not four. So "≈120 s per
   writing run, whatever it wrote" is not the rule either; something about the state the 8 h build left behind —
   free pages to reuse, a `partial` flag, an untouched `build_id` — made that run cheap. It is unresolved, and it
   is what instrumentation should answer first rather than a model fitted around it.

**What is not yet known, stated so the next round does not inherit a guess:** nothing in this codebase times a
build's phases. `IndexWriter` has two passes, an edge resolution, a community assignment and a `meta` write;
below them sits the engine's commit. The 120 s is somewhere in that stack and this measurement cannot put it in
one place — it can only rule *out* the file, the language and the scan, and rule *in* whatever is proportional to
the number of stored rows.

**Decision: the criteria do not move; the first lever does.** D17 put `InsertBatch` first because batching helps
the per-file part. The per-file part is ~2 s of these 120 (222 s in the 43-file run over a 120 s floor), so
batching is neither where the 8 h is nor where the two minutes a developer pays for editing one file are. What
the store holds comes first, and it holds far more than the text: 350.2 MiB for 13 575 passages is 27 KB each,
and the scratch workspace's 6.1 MiB for 114 passages is 56 KB each — which a 650-byte passage of text and a
512-wide vector of `int8` does not explain, even allowing for a store that rounds up to a floor. Then the two
cheap structural questions: can a reader stream the snapshot instead of materialising 13 575 rows with their
vectors, and can a commit cost the delta rather than the file.



