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

## D17 — Measured again (2026-11-01): one run, and the wrong conclusion drawn from it

**Withdrawn on its duration, the same day: see D19.** The machine was in Modern Standby from 11:29:57 to
19:40:11, so the 8 h 20 m is suspension, not work; the run was awake for 632 s. The counts and the bytes below
stand, the 61× stands, the budget-and-`partial` paragraph is resolved in D19, and every sentence that compares
this run's clock to D16's is struck. The entry is kept as written because a measurement that looks this
plausible should stay visible as one that was wrong.

**Measured**: `sharpcoreagent index` on this repository, .NET 11 Release, the machine D16 used. The database it
started from was the 233.4 MiB one earlier rounds had left behind; the duration is the run's own output and the
two timestamps on its log file, which agree; the size is the `.scdb` file afterwards.

| | D16 (2026-10-27) | this run (2026-11-01) |
|---|---|---|
| Files | 459 | 666 indexed (652 scanned: 590 added, 62 unchanged, 14 skipped) |
| Passages | 10 314 | 12 889 |
| Symbols · edges · communities | 4 571 · 5 284 · 329 | 6 268 · 8 227 · 582 |
| Wall clock | 513.5 s | **30 345 s**, of which 8 h 10 m was Modern Standby: **632 s awake** (D19) |
| CPU time | not recorded | 865 s, over those 632 s awake — it was working, at ~1.4 cores |
| Database | 69.2 MiB | **350.2 MiB** (367 165 440 bytes) |
| Source, `git ls-files` | 14.28 MiB claimed | 5.98 MB over 519 files |
| Index ÷ source | 4.8× | **61×** |
| Per passage | 49.8 ms | void: 632 s over 590 added files is 1.07 s each (D19) |

**Four things it said, in the order they mattered:**

1. ~~**Twenty-five percent more passages cost fifty-nine times the clock.**~~ **Struck (D19).** There is no clock
   comparison to make: the run was awake for 632 s, and 632 s over 590 added files is 1.07 s each against D16's
   1.12 s — the same rate, as a linear build should give. The "97% waiting, 3% CPU" figure was the wrapper
   process, not this one; measured on the child the run is CPU-bound at ~1.4 cores. D18 measures what a writing
   run actually costs, at a size where the number can be trusted.
2. **Nothing reclaims space.** `AutoVacuum` is off on the one place a handle is opened, and no code path
   vacuums a database that is only ever appended to and deleted from, so the file keeps whatever earlier builds
   left in it. That is how a 6 MB tree produces a 350.2 MiB index: 61× the source where SC-006 asks for 0.5×,
   against 4.8× when D16 measured it. *(D18 measured the other half of this: a later run that rewrote 43 files
   left the file byte-identical, so "every build adds to it" was too strong — and it still never comes down.)*
3. ~~**The command's own budget was not honoured.**~~ **Resolved in D19: it was honoured, and stopping was right.**
   `IndexCommand.CommandBudget` is one hour of wall clock, the machine spent 8 h 10 m of the run suspended, and
   the walk was truncated after 666 of the 704 files — of which the next run called exactly 38 "added", the 38
   this one never reached. So `partial` was true, and the flag is not the defect. What is wrong is the report: it
   prints 30 345 s and "run `sharpcoreagent index` to finish", which describes a build that ran out of budget
   rather than a clock that keeps counting while the laptop sleeps.
4. **D16's source size does not reproduce.** `git ls-files` sums 5.98 MB for the 519 tracked files of this tree
   today, and this tree is larger than the one D16 measured, not smaller. Either D16 summed a different set or
   one of the two figures is wrong. Nothing SC-006 concludes depends on it: 4.8× and 61× both miss 0.5×, and
   the fixed-floor argument D16 makes is untouched by either.

**Decision: the criteria stay, and the order of the fix is now measured rather than guessed.** D16 put batching
the row writes first (`InsertBatch` is in the engine's surface, and nothing in the index uses it yet); this run
adds the second step and the reason for its position — a build that batches its inserts into a 350 MiB file
still pays for 350 MiB of the file on every transaction, so reclaiming space is not cosmetic work that can wait
behind the batching. Then the same three numbers are measured again: passages, wall clock, bytes.

*(Superseded: D19 took the duration away from under this paragraph, and D18 replaced its premise with a measured
one. The ordering it argues for survives in D18's decision, for different reasons.)*

## D18 — Same day, at a size that can be trusted: what one writing run costs

**Of D17, this entry keeps the bytes and drops the reasoning.** Its "97% waiting, 3% CPU" came from sampling the
process the shell tool started rather than the indexer it spawned — measured on the child, writing is CPU-bound —
and D19 takes its duration away entirely. What D17 could not do, because it lumped one eight-hour run into one
number, is separate the parts. These six runs separate them.

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

**Four facts, and one rule that covers all six runs:**

1. **Walking and opening are not the cost.** A build that changes nothing walks 704 files, opens the store,
   counts four tables, compares hashes and prints a report in 5.5 s. Whatever a slow build turns out to be, the
   scan is not it.
2. **Writing anything costs about 120 s, and it does not matter what.** A markdown specification, the README and
   a C# file land within 12 s of each other (119.3 s, 123.7 s, 131.7 s), so the price is not the file's content,
   its size or its language.
3. **The price is fixed per run, and it grows with what the store already holds.** The same operation — one
   changed file — costs 0.7 s where 114 passages are stored and ~125 s where 13 575 are: 119× the rows for
   ~185× the fixed charge, which is 6–9 ms per *stored* passage per writing run. It does not scale with what the
   run writes; it pays for the whole store in order to touch any part of it.
4. **And the 43-file run stops being a mystery, because I did the arithmetic wrong.** 222 s for 38 added and 5
   changed is ~120 s of fixed cost plus ~2.4 s per written file — not the 86 minutes that 43 × 120 s implies,
   because the 120 s is per *run*, not per file. Fixed plus linear-per-file covers every run in the table: one
   file 120 + 2.4 ≈ 122, forty-three 120 + 43 × 2.4 ≈ 223, and one file in the scratch workspace, where the fixed
   part is under a second, 0.7.

**What is not yet known, stated so the next round does not inherit a guess:** nothing in this codebase times a
build's phases. `IndexWriter` has two passes, an edge resolution, a community assignment and a `meta` write;
below them sits the engine's commit. The 120 s is somewhere in that stack and this measurement cannot put it in
one place — it can only rule *out* the file, the language and the scan, and rule *in* whatever is proportional to
the number of stored rows.

**Decision: instrument before choosing, and do not start with batching.** D16 put `InsertBatch` first because it
would help the per-file part of a build; the per-file part is 2.4 s of a run that costs 122, so batching optimises
about 2% of the thing a developer feels when they edit one file. The fixed charge is proportional to the number
of *stored* passages; whether it is proportional to their *bytes* is not measured, and at 27 KB per passage
(350.2 MiB over 13 575) — 56 KB in a ten-file workspace, which 650 bytes of text and a 512-wide `int8` vector
cannot account for even with a store that rounds up to a floor — the bytes are the obvious suspect. So the order
is: time the phases first, since none of them is instrumented (two passes, edge resolution, community assignment,
`meta`, and the engine's commit underneath), then choose between trimming what a passage occupies, a commit that
costs the delta, and a reader that streams the snapshot instead of materialising 13 575 rows with their vectors.
Picking between those three without the phase numbers is how D17 went wrong, and D19 is why an unexamined
duration cannot carry a decision either.

## D19 — Where the eight hours went: Modern Standby, not the indexer

**The machine was asleep for 8 h 10 m of the run, and that is all of the missing time.** The evidence, all of it
from this machine:

- `Microsoft-Windows-Kernel-Power` id 506, *"The system is entering Modern Standby. Reason: Idle Timeout."* at
  11:29:57 on 2026-11-01, and id 507, *"The system is exiting Modern Standby. Reason: Input Mouse."* at 19:40:11.
  No ids 42 or 107 in that window, so this was Modern Standby (S0 low-power idle) and not classic S3 — which is
  exactly why the first check, which filtered on 42 and 107, came back empty and let D17 stand for an hour.
- The run's log file was created at 11:21:21 and last written at 19:42:07: 8 m 36 s awake before the machine
  slept, 8 h 10 m 14 s suspended, 1 m 56 s after the mouse woke it.
- 632 s of awake time against 865 s of CPU is 1.4 cores busy. The process was working whenever it was scheduled
  and had nothing to do while the system was in S0 idle, because it was not scheduled at all.
- Both the process's own `Stopwatch` and the supervisor's printed 30 345 s, and both agree with the log's
  timestamps, so the clock behind that number includes suspended time on this machine. Worth knowing, because
  that is the clock `CommandBudget` is computed from.
- This machine enters Modern Standby many times a day — 17 events on 2026-11-01 and 28–34 on each of the three
  days before, most of them `Reason: Idle Timeout` — so any build longer than the idle timeout is caught by one
  unless something holds the system awake. Nothing in this repository does.

**What that changes.** The build cost was never 30 345 s; it was 632 s, for a run that added 590 files to a
corpus of 12 889 passages — **1.07 s per file**, against D16's 1.12 s per file on a smaller tree. D16's per-passage
figure reproduces at this size (49 ms against 49.8 ms), so nothing here says the build regressed; what D16's rising
per-passage cost across its small corpora means is a separate question that two large runs cannot answer, and
D17's "fifty-nine times the clock" is not evidence about it either way. The build looked superlinear in D17
because D17 was measuring a closed lid.

**It also settles D17's budget paragraph in the code's favour.** `CommandBudget` is one hour of wall clock and
the run was suspended for 8 h 10 m, so the budget was spent by the time the walk resumed; the walk stopped after
666 of 704 files, `build_state` went to `partial`, and the next run reported exactly the 38 files this one never
reached. Both behaviours were right. The defect is narrower than D17 claimed: `partial` is honest, but the report
prints 30 345 s and "run `sharpcoreagent index` to finish", which reads as a build that is slow and gave up, when
what happened is that the laptop slept. A wall-clock budget cannot tell work from suspension, and neither can a
developer reading the number.

**Decision: no measurement of this tool counts unless the machine stayed awake, and the tool should say so.**
Two rules and one task:

- **For measurements.** Every run recorded here now carries wall clock *and* CPU time, which is what caught this:
  865 s of CPU inside 30 345 s of wall clock is not a slow indexer, it is a stopped one. Any figure whose wall
  clock is far above its CPU time is discarded until the standby log is checked. This applies to every slice in
  this repository, not to `002`.
- **For the tool.** `index` already has both numbers. It should print them together and say so when they disagree
  by more than a small multiple, so that a suspended build diagnoses itself instead of sending the next reader
  into `IndexWriter`. Its budget should count work — files, CPU — rather than a clock that runs while the machine
  is in S0 idle. A third option, worth naming and not deciding here: hold the system awake for the duration of a
  build (`SetThreadExecutionState` with `ES_SYSTEM_REQUIRED`, which belongs in the CLI and not in the domain), as
  a long build on a laptop is exactly the case that call exists for.
- **For the queue.** The phase instrumentation D18 asks for is cheap and independent of all of the above; the
  budget and reporting change is a separate small slice that does not need the phase numbers. Neither belongs to
  `002` T034's current round, which is a measurement round.

**What survives D17 and D18 after this:** the database is 350.2 MiB for 5.98 MB of source — 61× where SC-006 asks
for 0.5× — it does not shrink, and touching it with any write costs ~125 s at this size plus ~2.4 s per written
file. Those figures come from three runs at the keyboard with the machine awake, not from the eight hours, and
they are the ones the next fix should answer.

**Machine state afterwards, recorded so the next reader does not re-derive it (2026-11-01).** `powercfg /a` lists
Modern Standby only — no S3, no hibernation — so S0 idle is the one way this machine can stop a build. The active
scheme is *High performance*, and `powercfg /change standby-timeout-ac 0` has been applied: `Sleep after` now reads
AC 0 (never), DC 240 s, with the laptop on AC. Two settings that can still suspend are hidden from
`powercfg /query` and could not be read back without unhiding them first: **Lid close action** (a shut lid still
sleeps) and **System unattended sleep timeout** (`UNATTENDSLEEP`, which fires independently of the sleep-after
timer and is the usual reason a build sleeps on a Modern Standby machine even when sleep-after is *never*). If it
matters again:

```
powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP UNATTENDSLEEP 0
powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0   # 0 = do nothing, AC only
powercfg /setactive SCHEME_CURRENT
```

That second line means a shut lid keeps running, which is fine on a desk and is not fine on battery in a bag, so it
is set for AC only. None of this is a fix: it is one laptop's state, it does not travel with the repository, and it
is why the reporting change in the decision above matters more than the setting — a build that prints how much of
its wall clock was spent on the CPU diagnoses the next machine's surprise instead of inheriting it.

## D20 — The budget counts work, and the build report prints both clocks

**Decision**: a budget now names the clock it is measured on. `IndexBudgetClock.WallClock` is the default and what
the turn-start refresh passes — the thing waiting for it is a model, so its three seconds are the request's, not the
processor's. `ProcessorTime` is what `sharpcoreagent index` and `--rebuild` pass: an hour of processor time cannot be
spent by a machine that is not running the process. The report prints both numbers for the same window
(`wrote .sharpcoreagent/index in 120.5 s wall · 118.2 s cpu`), `--json` carries `cpuMs` beside `elapsedMs`, and a
`clock` line appears when the wall clock is more than twice the processor time by at least a minute.

**Why the clock is the caller's choice rather than a property of the indexer.** Both callers exist and want opposite
things. `--index-budget` bounds how long a model waits; measuring that in processor time would let a loaded machine
hold a turn for longer than the developer allowed. A command bounds how much work may happen; measuring that in wall
clock is what D19 destroyed, because the allowance was spent while the process was not running at all. So
`EnsureFreshAsync` and `RebuildAsync` grew an overload that takes the clock, and the two-argument forms remain the
wall-clock shorthand they always were. The wait for the writer lock is the one place the two disagree *inside* a
build, and it is settled by charging neither: a wall-clock caller spends the wait from its budget because it is
waiting, and a processor-time caller gets `LockWaitBudget` (30 s, the number a rebuild already used) and then its
whole allowance, because queueing behind another process is not work done.

**Why `Environment.CpuUsage`.** It is BCL, it needs no process handle, and in `net11.0` it returns an
`Environment.ProcessCpuUsage` whose `TotalTime` is what a budget wants — the first draft subtracted the values
themselves and the compiler refused, which is the one thing that API is not. The process-wide figure is the honest
one for a command, which is one process doing one thing, and it includes the engine's threads and the GC: exactly
the "what did this cost" question a report asks. Reading it once per scanned file is one call more per file at 704
files, invisible next to the per-file work it guards.

**Why the report has a floor and a factor.** A line that fired on every build would be a line nobody reads, and a
build that waits on storage is ordinary here — the per-file commit looks cheap and is the expensive part. Below a
minute of difference, the thread pool and one blocking read explain everything, so the `clock` line needs both a
minute and a factor of two. Above that the shape is unambiguous: `8 h 25 m` of wall clock against `14 m 25 s` of
processor time is 35×, and the sentence names the power events to check instead of asking the reader to remember
D19. The refresh line still prints only the wall clock, because a three-second wait for a model is a wall-clock
quantity by definition and the extra token buys nothing there.

**What it does not do.** It changes no measurement and fixes no cost. The 120 s fixed charge per writing run and
SC-006's 61× are untouched; the walk still stops when the allowance runs out, it just cannot be spent by a nap any
more; and the phase instrumentation T034 opens with is still the next thing to write. What this buys is that the
next surprise is printed rather than inherited: the eight hours cost half a day because the only number on screen
was the one that lied.

**What proves it.** `BuildTimingTests` pins the arithmetic and the renditions — `2 m 2 s wall · 1 m 58 s cpu` for an
ordinary run, 35× for D19's, nothing under the floor, nothing when several cores make processor time exceed the wall
clock. `IndexCommandTests` asserts the line and the JSON key from a real build. `RefreshBudgetTests` proves the
processor clock stops the walk at a zero allowance, and that a generous one still finishes the workspace. Suite
1 309 → 1 319, build clean at zero warnings.






---

## D21 — A build's phases, measured for the first time (2026-11-02): two whole-index steps are the run

D18 could say that a writing run costs 119–132 s whatever it writes, and could not say which part of the build
spends them. Nothing timed a build's steps, so the next hour of performance work would have been aimed at a guess.
This slice adds the measurement and changes nothing it measures.

**Where the clock lives.** `IndexPhaseClock` (internal, `SharpCoreAgent.Indexing`) keeps a total per step and times
work by wrapping it: `Measure(phase, () => …)`, `MeasureAsync` for a `Func<Task>`, and a `Func<ValueTask<T>>`
overload for `CommunityAssigner.AssignAsync`. Wrapping rather than opening and closing a scope by hand is what makes
a step impossible to leave open by a changed control flow. Every step of `IndexPhases.Order` exists from
construction with a zero total, so two reports always list the same lines and a reader can add them up; a name that
is not in the order is refused, because a rename would otherwise cost a step's time silently. `IndexWriter.For`
creates the one clock of a build and hands it to `IndexFileWriter`, so the store's commit — timed inside
`RunInBatch`, at both ends of the batch — lands in the same report as the steps that contain it. `IndexBuildReport`
carries `Phases`, `--verbose` prints them, `--json` gains a `phases` array, and a prune reports none because it is
not a build.

**Why the commit is one nested line and not four.** It runs inside `write`, `remove`, `edges` and `meta`. Four lines
of commit scattered through four differently sized phases read as four unrelated costs, and the question they answer
— what the store charges for being touched — is one question with one answer. `Nested` says so on the report and the
command line prints the line as `of which commit`, without a share, because its seconds are already inside the lines
above it.

**Four runs on this repository** (lean, hashed-local/512, 350.2 MiB of index):

| phase | rebuild: 698 files, 17 m 51 s | 26 files, 2 m 59 s | 6 files, 2 m 28 s | 0 files, 5.5 s |
|---|---|---|---|---|
| `scan` | 0.1 s | 0.1 s | 0.1 s | 0.1 s |
| `load` | 0.0 s | 0.0 s | 0.0 s | 0.0 s |
| `write` | 15 m 47 s (88 %) | 1 m 2 s (35 %) | 17.1 s (12 %) | 0.1 s |
| `remove` | 0.0 s | 0.0 s | 2.0 s (1 %) | 0.0 s |
| `edges` | 1.8 s | 1.9 s | 1.6 s | 0.0 s |
| `communities` | 1 m 56 s (11 %) | 1 m 49 s (61 %) | 2 m 0 s (81 %) | skipped |
| `meta` | 1.9 s | 1.8 s | 3.0 s | 1.8 s |
| of which `commit` | 15 m 49 s | 1 m 3 s | 22.3 s | 1.8 s |
| `other` | 3.7 s | 3.5 s (2 %) | 4.3 s (3 %) | 3.5 s (64 %) |

**What that says.** Every number here was in the report before this slice except the split, and the split is the
whole finding.

*The commit is the write.* A rebuild is 88 % commit and a run that edits six files is 12 % write against 22.3 s of
commit — and in both cases `write` and the commit inside it agree to within two seconds over the whole phase, so
whatever the rows cost, it is spent in `EndBatchUpdate` rather than in the code between the batch's two ends. The
commit also has a price per batch: this build makes one per written file plus one each for `remove`, `edges` and
`meta`, which is 700 batches in the rebuild, 28 in the 26-file run and 9 in the 6-file run — 1.36 s, 2.25 s and
2.5 s per batch, and the rebuild's lower figure is the same price against a store that grew into its final size.
So a commit costs more the bigger the index is, and the build pays it once per file.

*Community assignment is a fixed charge per writing run.* 1 m 49 s for 26 files, 2 m 0 s for 6, 11 % of a rebuild
and 81 % of the small run: it does not care what changed, only that something did, because it clusters the whole
graph. That is the number D18 could not see when it reported 119–132 s for one changed file — one commit's worth of
writes plus one Louvain pass plus four seconds of opening the store lands in exactly that window.

*A run that changes nothing is not free.* 5.5 s, of which 3.5 s is the writer lock, opening 350 MiB and reading the
status back, and 1.8 s is the `meta` write — a batch of its own, committed to record that nothing happened.

**What that changes about the plan.** One lever moves every run: fewer commits. The per-file batch was chosen because
it is the largest unit that can fail independently ("a build that dies on file 40 leaves files 1–39 committed"), and
that property is worth keeping, so the fix is not one batch for the whole build but a checkpoint interval — a bounded
amount of re-work for a bounded multiple of the speed. Second, for a writing run only: `communities`, which is ours
and recomputes a graph that changed by six files. Third, and cheapest: the `meta` commit and the store open, which a
turn-start refresh pays every time it costs a model its wait. None of that is in this slice.

**What it does not do.** It fixes no cost and changes no decision. SC-002 and SC-006 are untouched, `--rebuild`'s
fixed charges are untouched, and the numbers above are one machine on one day — the useful part is the ratio between
the lines, not the seconds on them.

**What proves it.** `IndexPhaseClockTests` pins the arithmetic: every phase present in build order before anything
ran, only the commit nested, two entries of one phase adding up instead of replacing each other, an awaited step
timed across its await, and a name that is not a phase refused. `PhaseTimingTests` reads a real build's report and
asserts the wiring end to end: all eight phases in order, `scan`/`load`/`write` costing something, the phases adding
up to no more than the run they belong to, the nested commit staying inside the four phases that contain it, and a
prune reporting none. `IndexCommandTests` asserts the `--verbose` block, its absence without the flag, and the
`phases` array in `--json`. Suite 1 319 → 1 332, build clean at zero warnings.

---

## D22 — The commit lands once per checkpoint (2026-11-02): the write phase falls 25×, and the clusterer becomes the run

D21 ended with three levers and a warning: fewer commits moves every run, and the per-file batch is "the largest unit that
can fail independently … a checkpoint interval" is what to build instead of one batch for the whole build. This slice is
that interval, shipped as spec `026-checkpointed-index-writes`.

**What shipped.** `IndexFileWriter` grows a checkpoint: `BeginCheckpoint` marks a group, `EndCheckpoint` commits it,
`AbandonCheckpoint` discards it, and `RunInBatch` joins an open checkpoint instead of opening its own batch. The batch is
opened lazily by the first write inside the group, so a checkpoint that carries nothing costs nothing and a build stopped
before its first file still pays exactly one commit. `IndexWriter` carries 32 files per checkpoint, commits before it
breaks for its budget and before cancellation leaves the writer, and abandons on any other exception; `RemoveVanished` and
`PruneAsync` remove their whole set in one commit each. `IndexWriteResult.Commits` counts every `EndBatchUpdate` where it
is timed, `index --verbose` prints it, and `--json` does not — the count is a fact about the writer's strategy, not about
the index. Nothing else moved: one file is still written as one unit, `meta` is still written last and alone, and the
schema, the reader, the tools and the published report shape are untouched.

**Why not one batch for the build.** It would be 700 → 1. A checkpoint keeps the failure unit the per-file batch existed
for, shaped like the cost: a crash or a cancellation loses at most one group's writing — 32 files, about 1.6 s here.

**A rebuild of this repository** (740 files, 14 200 passages, 525.2 MiB; the index has grown since D21's 698 files and
350.2 MiB, and a commit costs more the bigger the index is, so the ratio below is a floor):

| | D21 | D22 |
|---|---|---|
| the run | 17 m 51 s | **2 m 53 s** |
| `write` | 15 m 47 s (88 %) | **36.9 s** (21 %) |
| of which `commit` | 15 m 49 s | **37.5 s** |
| `communities` | 1 m 56 s (11 %) | 2 m 8 s (74 %) |
| commits | **700** | **26** |

26 is arithmetic: ⌈754 ÷ 32⌉ = 24 checkpoints for the 740 indexed and 14 skipped files, plus the edges and `meta`. The
write phase and the commit inside it still agree to within a second, which is the same finding D21 made, moved down by a
factor of 25.

**What that leaves.** `communities` is now three quarters of a rebuild and it is a fixed charge per *writing* run, so the
next slice is D21's second lever rather than more of this one. Then the no-op `meta`/open overhead (5.5 s a turn), then
the checkpoint's own size: at 32 files the commit is still 48 % of the write phase, and a group of 128 would take another
nine seconds off this rebuild — but a checkpoint is also a buffer held in memory until the commit, and that is a decision
about memory this slice did not take.

**What proves it.** `CheckpointTests` (9): five files commit 3 times at the default and 5 times at two files a group, with
the same counters and the same rows either way; an abandoned checkpoint leaves the store exactly as the last commit left
it, and a checkpoint committed before a failure stays; a zero budget commits only `meta`; a budget that expires inside a
checkpoint keeps the group (200 files, 100 ms, and the rows equal the counter); an already-cancelled build writes nothing;
a prune of three files commits once; a second begin is refused. `IndexCommandTests` adds the `--verbose` line and the
absence of a `commits` key in `--json`. Suite 1 483 → 1 494, build clean at zero warnings.


---

## D23 — The clusterer's own steps (2026-11-02): two of five, and neither is the one the phase looked like

D22 ended with a sentence that was a promise rather than a finding: "`communities` is now three quarters of a rebuild and it
is a fixed charge per *writing* run, so the next slice is D21's second lever." This slice is the first half of that lever —
saying which part of the phase the two minutes is — shipped as spec `027-community-pass-steps`.

**The same lesson, one level down.** D21's finding was that the write phase looked like extraction and was a commit. A phase
name is a label, not a decomposition, so `communities` could have been a table scan, a statement loop, a commit, or the
algorithm. Five suspects, two orders of magnitude apart, and the round that acts on this phase without splitting it is the
round D21 was written to prevent.

**What shipped.** `AssignAsync` takes the build's clock and records five steps: `nodes` (the declarations, read and ordered),
`adjacency` (the relations, read into neighbour lists and renumbered into the graph), `louvain` (the partition and the
numbering that makes its labels reproducible), `degree` (the metric) and `assign` (both columns, written back). All five are
nested inside `communities`, printed as `of which …`, and take no share of the run; `IndexPhases.IsNested` became a lookup
over six names. `PartitionAsync` split into `ReadGraph`, `BuildGraph` and `RankAsync`, and the clock gained a `Func<Task<T>>`
overload because the store's algorithms return `Task<T>` while the build's own awaited steps return `ValueTask<T>`.

**A commit that was invisible twice.** The clusterer writes both columns in its own batch rather than through
`IndexFileWriter`, so its commit sat inside the phase's wall clock and outside both `commits` and the `commit` line: a build
made 27 commits and reported 26. Both ends are now charged to `commit` and the commit is counted, so the rule from D22 —
count it where you time it — covers the two places that commit instead of one. Three assertions in `CheckpointTests` moved by
one, which is the cheapest possible proof that the old number was short.

**One rebuild of this repository** (740 files, 14 231 passages, 7 305 symbols, 10 806 edges, 645 communities, 525.2 MiB;
3 m 2 s wall, 3 m 7 s cpu, 27 commits):

| step | wall | share of the phase | what it is |
|---|---|---|---|
| `nodes` | 0.1 s | — | 7 305 declarations read and ordered |
| `adjacency` | 0.2 s | — | 10 806 relations read, then renumbered |
| `louvain` | **53.4 s** | **40 %** | the partition of 7 305 nodes, and its numbering |
| `degree` | 0.0 s | — | the metric over the same graph |
| `assign` | **1 m 21 s** | **60 %** | 7 305 `UPDATE` statements in one batch |
| `communities` | 2 m 15 s | 74 % of the run | the phase itself, unmoved |

**What that says.**

*The reads are not the cost.* 7 305 declarations and 10 806 relations cost 0.3 s together. That is worth writing down because
the write phase's history made a slow store read feel likely, and because it means a faster read path would buy nothing.

*`assign` is the largest part, and it is the statement, not the commit.* 1 m 21 s for 7 305 `UPDATE`s is 11 ms each, against
about 1.7 ms per statement for a file's mixed inserts in the write phase. The commit at the end of the batch is inside the
41.6 s the `commit` line reports, nearly all of which is the writing phase's 24 checkpoints — so the next round can price the
loop without untangling a store charge from it. What makes an `UPDATE` six times a mixed insert is not yet known: the
statement's shape, the `symbols` indexes, or the size of the batch.

*`louvain` is 53.4 s over a graph that `degree` crosses below the report's resolution.* A partition of 7 305 nodes with 10 806
edges is a second's work in most implementations, so the number is about the algorithm rather than about this repository. It
is in third-party code, which makes it a smaller lever than `assign` — but a 40 % step is still a step.

*Neither is proportional to the change.* A run with **two changed files** costs **2 m 39 s** on this index: `assign` rewrites
all 7 305 rows and `louvain` re-partitions all 10 806 edges whatever was edited. D18 could say a writing run pays a *fixed*
charge of 119–132 s and could not say what it was; the fixed charge is now two named steps, and that is what stands between
SC-002's "one changed file inside 3 s" and the truth.

**What that changes about the plan.** Two levers, `assign` first (60 % of the phase, and ours) then `louvain` (40 %, and the
store's). The second can be answered without a fork by giving Louvain a coarser graph or a bounded candidate set, which is a
decision about label stability and therefore a spec of its own. D21's third lever — the no-op `meta`/open overhead, 5.5 s a
turn — is still open, and after this round it is the cheapest fixed charge left. SC-002's 5 000-file tree and SC-006's 61× are
untouched.

**What it does not do.** It fixes no cost. The phase is 2 m 15 s before this slice and 2 m 15 s after it, and the run is 3 m 2 s
both times; the only change a developer would notice is that the report now says where the time goes. Two of this slice's own
numbers are 0.0 s, which is information — the step is real and too small to see — and not a missing measurement.

**A run that changed nothing reports five zeros, which is the point.** An incremental run over the same index — 0 written,
0 removed — skipped the pass as it always did and printed `communities 0.0 s` with five `of which` rows at 0.0 s, one commit
(`meta`, 2.4 s) and the block's 60 % in `other`. The split does not invent work a build did not do, and a reader can tell a
skipped phase from a cheap one by the row above it.

**What proves it.** `IndexPhaseClockTests` pins the nested set, the order of the clusterer's names, and that a step producing a
value is timed across its await and hands the value back on both overloads. `PhaseTimingTests` pins the wiring end to end: the
five rows on a real build, `louvain` and `degree` at exactly zero when the corpus declares no relation, the commit staying
inside the phases that contain it, and the block still adding up to no more than the run. `CommunityAssignerTests` pins the
positive case — two clusters, all five steps nonzero and nested. `CheckpointTests` pins the count including the clusterer's
commit, and `IndexCommandTests` pins thirteen phases in `--json` and `of which nodes` in the terminal. Suite 1 494 → 1 498,
build clean at zero warnings.

## D24 — The write-back without a parser (2026-11-03): 1 m 21 s becomes 1.8 s, and the algorithm is now the run

D23 ended by naming two levers and taking neither: `assign` (60 % of the phase, and ours) and `louvain` (40 %, and the
store's). This slice takes the first — spec `028-row-addressed-symbol-writes` — and it was not a tuning exercise. `assign`
was 7 305 `UPDATE` statements; the pass now tells the store which row and which two columns, and lets it do what a table
already knows how to do.

**The measurement came before the change.** A temporary probe on a controlled table — 3 001 declaration rows in one index,
one batch each way, same values, same seven labels, run twice so a cold page cache could not flatter whichever went first:

| 3 001 rows, one batch | cold | warm |
|---|---|---|
| `ExecuteSQL` with bound parameters | 14 483 ms | 12 875 ms |
| `IndexSql.Update` (`UpdateByPrimaryKey`) | 86 ms | 143 ms |

The statement path formats every value back into statement text, parses the text, and binds what it parsed — per statement.
The row path skips all three and keeps the one cost both share, the index maintenance. Two orders of magnitude between them,
on the same store, in the same batch: the guess D23 wrote down as "the statement's shape, the `symbols` indexes, or the size
of the batch" was the first of the three, and the batch was never a suspect worth ranking (4.5 ms per statement in a batch
of 3 001, 11 ms on the real index — the per-statement price does not move with the batch).

**What shipped.** `CommunityAssigner.Write` keeps its batch, its two `commit` measurements and its counted commit, and
inside the loop it calls `IndexSql.Update` — the write `IndexFileWriter.WriteMeta` has used for `meta` since that table
existed and that no path had ever measured. The bag is built per row rather than reused, because the store may keep the
reference until the batch flushes and a shared bag would then give every row the last row's values. `UpdateSymbol` lost its
last user and was deleted: there is no `UPDATE` statement left in `src/`.

**One rebuild of this repository** (761 files, 14 287 passages, 7 311 symbols, 10 840 edges, 623 communities, 525.2 MiB;
1 m 56 s wall, 2 m 0 s cpu, 27 commits). The corpus grew by this round's own spec files between the two runs, which is why
the counts move by less than a percent in the table and the community count moves by a few dozen:

| step | round 18 | round 19 | what it is |
|---|---|---|---|
| `nodes` | 0.1 s | 0.0 s | the declarations, read and ordered |
| `adjacency` | 0.2 s | 0.0 s | the relations, read and renumbered |
| `louvain` | 53.4 s | **1 m 8 s** | the partition, and its numbering |
| `degree` | 0.0 s | 0.0 s | the metric |
| `assign` | **1 m 21 s** | **1.8 s** | both columns, written back |
| `communities` | 2 m 15 s | 1 m 10 s | 61 % of the run |
| the run | 3 m 2 s | 1 m 56 s | 27 commits, both times |

**What that says.**

*The parser was the cost, and it was 45×.* `assign` fell from 81 s to 1.8 s. What remains is 0.25 ms per row on a table
whose rows carry thirteen columns and four indexes, against 40 µs per row on the probe's warm table — that difference is
the index maintenance the statement path was paying too, which is why the probe's own "86 ms" is the floor of a cost this
table cannot reach. The floor was never zero and does not need to be.

*`louvain` is now the phase, and the run.* 1 m 8 s of 1 m 10 s, 58 % of the rebuild. D23 called it "a smaller lever than
`assign`" on the grounds that it is third-party code; it is now the larger one, and it is the one that makes a two-changed-
file run cost what a full one costs. Making it smaller means a coarser graph or a bounded candidate set, which changes what
the labels mean — a spec of its own, and the next round's subject rather than this one's.

*The commit did not move, and now it is the second-largest line.* 27 commits both times, `commit` 40.6 s of the run, of
which the clusterer's is one. D22's finding stands untouched: the commit is the write, and this round made the writing
between the commits cheap enough that the commits are visible as themselves.

*Nothing about the index changed except its `community` column's cost.* Same schema, same indexes, same rows, same labels
for the same corpus, same 27 commits, same report shape, no new type or flag, and `--json` unchanged. `index` still reports
`state fresh`, and `index --verify` still finds 761 rows in the index for 761 files on disk with 0 hash mismatches and 0
orphans.

**What proves it.** `IndexSqlUpdateTests` (new, three cases) pins the three facts the change rests on: a row-addressed
update inside a batch is stored by `EndBatchUpdate`, is discarded by `CancelBatchUpdate` — the promise the pass's single
batch makes to an interrupted build — and changes only the columns it names. `CommunityAssignerTests` gains the case at the
pass's own level: every declaration keeps its key, name, kind, line range and `qualified_name` through a rebuild, and the
one isolated declaration still gets an empty label rather than an omitted column. The pass's existing cases — two clusters,
identical labels across two builds, five nonzero steps, the centrality ranking — pass unmodified. Suite 1 498 → 1 502,
build clean at zero warnings.

**What it does not do.** It does not touch `louvain`, the `meta`/open overhead (5.5 s a turn, measured again by the no-op
`index` run at 6.0 s), or SC-002's 5 000-file tree. It does not drop `idx_symbols_community` to buy the residual back; the
probe says the residual is 1.8 s and dropping an index would change the plan of every query that reads a community.

## D25 — The partition without the store (2026-11-03): 1 m 8 s becomes 0.1 s, and the partition gets better

D24 ended by naming the last lever and stepping around it: `louvain` at 1 m 8 s, "in third-party code", and smaller only
at the price of "a decision about label stability — a coarser graph or a bounded candidate set". This slice — spec
`029-modularity-partition` — takes it, and the measurement refused both of the options D24 expected to have to choose
between. The labels keep their meaning, the partition scores 34 % higher than the one it replaces, and nothing about it is
an approximation.

**The measurement came before the change.** Three temporary probes on a copy of this repository's own index, deleted with
the round. The graph first, because the size of the problem decides which levers exist: 7 311 declarations, 10 840 relation
rows (calls 10 095, doc_mentions 635, implements 89, inherits 21), **4 123** with at least one relation, 10 744 distinct
undirected edges, 3 188 with none, largest degree 904 — and **3 999 of the 4 123 are one connected component**, so
decomposing the work by component, the first idea, was worth 1.7× (42.665 s) and returned a different partition.

| probe | measured |
|---|---|
| the store's `LouvainAlgorithm`, the whole graph | **71.100 s**, 12 iterations, 623 communities, its own objective 1.001678 |
| the stored partition, scored by the definition | 0.490230 |
| ours, local moving only | 0.0167 s, 7 sweeps, 651 communities, modularity 0.475347 |
| ours, local moving plus aggregation | **0.1466 s**, 5 levels, 59 communities, modularity **0.655454** |
| ours, run twice | identical |
| how much of today's partition survives | 603 of the 623 communities whole; the largest grows from 196 to 494 |

**What the first row is.** `LouvainAlgorithm.ExecuteAsync` calls `CalculateModularity` — a LINQ `GroupBy` over every node,
then a scan of every adjacency list — once for the starting partition, once per iteration, and once per *candidate move*.
The graph offers 25 611 candidates per sweep, so twelve sweeps is about 307 000 full-graph scans of a 4 123-node graph, each
allocating a dictionary of 4 123 entries. That is the 71 s. It is not the graph, and it is not the platform: it is an
objective recomputed from scratch a third of a million times when the weights a move needs are already in hand.

The second thing the same probe found is the one that decided the round. The implementation reports 1.001678 for its own
partition, and modularity cannot exceed 1: it counts every internal edge from both endpoints, so what it maximises is
`2·(internal fraction) − Σ(d_c/2m)²`, not `Σ[l_c/m − (d_c/2m)²]`. Scored by the definition it is named after, the
partition this index has been storing scores **0.490230**.

**What shipped.** `ModularityPartition`, in `SharpCoreAgent.Indexing/Graph`, three private steps and one public method:
local moving on a weighted graph with self loops, coarsening that turns communities into nodes and keeps a community's
internal weight as a self loop, and a level loop that stops when a level merges nothing. `CommunityAssigner.RankAsync`
became `Rank` and calls it; the `CommunityDetection` namespace left the pass, and `DegreeCentrality` stayed, because the
degree step is linear and is not this round's subject. The `louvain` step keeps its name: it is still Louvain's local
moving, and the record compares step names across rounds.

The design was measured rather than chosen. Single-level local moving alone is 3 % *worse* than what it replaces
(0.475347 against 0.490230) — doing the arithmetic correctly but stopping after one phase would have been a slower-looking
answer, not a faster one. The hierarchy costs 0.13 s more and lands 34 % above it, which is why the second phase is the
round rather than decoration.

**One rebuild of this repository** (766 files, 14 348 passages, 7 330 symbols, 10 867 edges, **58 communities**,
787.9 MiB; 55.6 s wall, 1 m 1 s cpu, 27 commits — run twice, identical, and `--prune` reproduces the same file):

| step | round 19 | round 20 | what it is |
|---|---|---|---|
| `nodes` | 0.0 s | 0.0 s | the declarations, read and ordered |
| `adjacency` | 0.0 s | 0.0 s | the relations, read and renumbered |
| `louvain` | **1 m 8 s** | **0.1 s** | the partition, and its numbering |
| `degree` | 0.0 s | 0.0 s | the metric |
| `assign` | 1.8 s | 2.4 s | both columns, written back |
| `communities` | 1 m 10 s | **2.6 s** | 5 % of the run |
| `write` | ~40 s | 44.1 s | the fourteen thousand passages |
| `meta` | ~2 s | 2.5 s | the one commit's other end |
| the run | 1 m 56 s | **55.6 s** | 27 commits, both times |

**A writing run, which is the number D23–D25 kept naming.** `index --verbose` over a tree where one file had changed:
**11.3 s** (11.5 s including the process), of which `communities` **2.2 s** (`louvain` **0.1 s**, `assign` 2.0 s, `commit`
7.0 s) — against round 19's 1 m 10 s clusterer for the same shape of run, and against the 5.8 s a run that changes nothing
still costs (`index` with 766 files unchanged, 5.8 s, measured on the same day). The fixed charge the clusterer added to
every writing run is gone; what is left of that run is the commit and the open, which are `002` T034's own items.

**What that says.**

*The partition was never the expensive part.* 71.100 s to 0.1 s on the same graph, the same nodes and the same edges: a
680× on the step and 26× on the phase, and a rebuild from 1 m 56 s to 55.6 s. The `louvain` step has fallen out of the
report's share column, which is what a step that costs a tenth of a second should look like beside `write`.

*The answer got better, not just faster.* Modularity 0.655454 against 0.490230 for the partition it replaces; 59 clusters
of 70 declarations on the probe's copy of the graph — 58 once this round's own five files joined it — instead of 623 of
6.3; 603 of the 623 old clusters whole inside a new one, largest 196 → 494. The
clusters may merge, they do not scatter. That is the label-stability answer D24 deferred: a label still names the
declarations that belong together, the numbering rule did not move (`c0…` by each community's ordinally-first member), and
the same corpus still stores the same labels twice — which is a test (`TheSameGraphTwiceIsTheSamePartition`,
`TwoDisjointClustersGetTwoLabels`) rather than a claim.

*What the store was optimising was not modularity.* Its own number exceeded 1.0, which the definition forbids, because
internal edges were counted twice. This is worth recording beside the speed: a function that names a well-defined quantity,
computes a different one, and is 485× slower than the computation it names is not a dependency worth keeping for one step.

*Nothing else moved.* Same schema, same columns, same indexes, same `--json`, same 27 commits, same report shape, and
`index --verify` finds 766 rows in the index for 766 files on disk with 0 hash mismatches and 0 orphans. `status` reports
`fresh` and 58 centers.

*One number this round cannot explain.* The index file is **787.9 MiB** where round 19 reported **525.2 MiB** for the same
corpus, and `--rebuild` (twice) and `--prune` all reproduce today's figure, so it is the store's data rather than a
one-off. The round changes nothing the writer stores except a column of short label strings, and the labels got shorter
(58 values instead of 623), so this slice is not its cause — but "not my cause" is not an explanation, and the size is
already on `002` T034's list. It stays there, unpriced and named, rather than being quietly dropped from the record.

**What proves it.** `ModularityPartitionTests` (new, six cases): two triangles that never meet are two communities; two
triangles joined by one edge are still two, which is the case that separates a partition from a connected-components walk;
an edge is one community and a lone node is its own; no single move improves the answer it returns, checked by scoring
every legal move against the definition; the same graph twice is the same partition; and twenty triangles in a ring merge
below twenty, which only the level above the first can do. `CommunityAssignerTests` keeps its cases unmodified — two
clusters, identical labels across two builds, an isolated declaration's empty label, the five measured steps with `louvain`
and `degree` at exactly zero when the corpus has no relation. Suite 1 502 → 1 508, build clean at zero warnings.

**What it does not do.** It does not touch `degree` (0.0 s, and the store's), the graph's definition (every relation,
undirected, unweighted — weighting `calls` above `doc_mentions` is a decision about what a label means), or any knob: no
resolution parameter, no sweep budget, one behaviour. It does not price the index size above, the no-op `meta`/open
overhead (5.8 s a turn, unchanged and re-measured), the checkpoint, SC-002's 5 000-file tree or SC-006's 61×. Those are
`002` T034's, and T034 is a smaller item than it was: a writing run now costs 11.3 s rather than 1 m 16 s, and what stands
between it and "one changed file inside 3 s" is the commit and the open rather than anything the clusterer does.



## D26 — The file the index left behind (2026-11-03): 787.9 MiB becomes 73.2 MiB in 0.6 s, and no row moves

D25 ended with one number it could not explain: the index file was **787.9 MiB** where round 19 measured **525.2 MiB** for
the same corpus, twice reproduced by `--rebuild` and again by `--prune`. Round 21 answered it by asking the store the only
question that produces an answer — *how much of this file are you no longer using?*

*The probes, before any code.* Four temporary ones, on a copy of this repository's index and on a synthetic one, deleted
with the round:

| probe | measured |
|---|---|
| the file | 787.9 MiB; `GetStorageStatistics()`: used 675.6 MiB, free 112.2 MiB, fragmentation **0.0**, 16 blocks, 1 037 pages, 28 732 free pages |
| the rows | 14 368 passages · 7 336 symbols · 10 879 edges · 767 files; every passage's text 9 153 680 characters, its `tokens` 6 172 832, every heading, doc and signature 1.7 M — **24 MB of characters** |
| `VacuumAsync(VacuumMode.Full)` | **787.9 → 73.2 MiB in 0.6 s**, reclaimed **714.6 MiB (90.7 %)**, and the rows unchanged: 14 368 · 7 336 · 10 879 · 767 |
| the same call through `IndexHandleCache` | 0.7 s and the same numbers, with the process's own handle open — no `CloseAll` needed |
| `VacuumMode.Quick`, `VacuumMode.Incremental` | **0.0 MiB**, 0.0 s each |
| a one-file index, grown by hand | 6.1 MiB → 14.1 MiB after 2 000 rows of 4 KiB → `DELETE` → **still 14.1 MiB** → `Full` → **6.1 MiB in 0.02 s**, two live passages intact |
| 2 000 single-row updates on it | **57.3 s** (28.6 ms each), size unchanged |

*What the rows say.* The index is 10.8 times its data, and the store can hand the difference back in two thirds of a
second without losing a row. A delete frees the row and not the page — the synthetic file proves it at a size a test can
hold — and nothing in this product ever asked the store to compact what that leaves. That is the whole explanation for
D25's number: 525.2 MiB was already inflated, 787.9 MiB was the plateau after more writing, and both figures are the file
rather than the corpus. The plateau is stable because the store re-uses what it frees; it simply never shrinks the file.

*Why the round could not trigger on a measurement.* `StorageStatistics` reports `used` 86 % / `free` 14 % for the bloated
file and `used` 85 % / `free` 15 % for the compacted one, with fragmentation **0.0 in both states**. Those percentages
describe the file the store is managing, not the distance between the file and its rows; the only way to learn what a
compaction would leave is to run one. So the trigger is the operation and not a ratio: `index --rebuild` compacts what it
wrote, `index --compact` compacts on demand, and the turn-start refresh never compacts because its budget is three
seconds and a rewrite scales with live data.

*The side finding, recorded and not acted on.* A single-row `UpdateByPrimaryKey` costs **28.6 ms** on this store and does
not grow the file. That is the same cost round 19 measured at 11 ms per `UPDATE` statement for 7 305 of them, and it is
three orders of magnitude above what a row update should cost — a fact for whoever next writes a column by hand, and
outside this round's slice.

*What it changes.* `SharpCoreDbIndexMaintainer.CompactAsync` (the store's `Full` vacuum under the writer lock, reporting
before/after/reclaimed/elapsed and answering a lost lock or a refusal rather than throwing), the same core called at the
end of a rebuild that finished the workspace, `index --compact` with a prose block and a `--json` document, and one note
in the rebuild's report. No schema, no column, no persisted state, no new commit.

*What proves it.* `CompactionTests` (new, four cases): dead space is given back and the passages, the file row and the
hashes survive; a second compaction reclaims 0 and succeeds; a workspace nobody indexed creates no database and says
`there is no index to compact`; a rebuild compacts what it left behind and says so in a note — and the note comes first,
because a repository with skipped binaries produces more notes than the ten a report prints, which this round learned by
running it. `IndexCommandTests` adds two — the block's before/after/content/state, and a JSON `afterBytes` equal to the
file on disk — `IndexCommandParsingTests` adds two — `--compact` refuses `--status`/`--verify`, `--prune --compact` is
allowed — and the refresh gate's fake maintainer answers the new member. Suite 1 508 → 1 516, build clean at zero
warnings.

*The round on its own corpus.* `index --rebuild` writes **165.4 MiB** and its note reads `compacted 91.9 MiB in 0.5 s
(165.4 MiB -> 73.5 MiB)`; the file on disk is 73.5 MiB; the run costs 51.9 s, which is round 20's 55.6 s without the
rewrite's 0.5 s and with a smaller file to write; `index --verify` finds 771 rows for 771 files on disk (766 before this
round's own five files were added), 0 hash mismatches, 0 orphans. The same rebuild twice leaves 165.4 MiB both times,
which is the arithmetic the round was missing: a rebuild spends about 1.25× the live data on pages it stops using, and
nothing was ever giving them back.

*What it does not do.* It does not compact on a writing run (a turn's budget is 3 s and this rewrite scales with live
data), and it does not fix the writing-run floor: the `meta` batch still costs 2.1 s after the rewrite against 2.9 s
before, and an open is 0.05 s either way. What it does fix is the file and everything that scans it — the first full
`chunks` scan is 2.6 s on the bloated file and 0.007 s on the compacted one. SC-002's 5 000-file tree, SC-006's 61× and
the commit line stay open on `002` T034, which is now one item shorter than it was.
