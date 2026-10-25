# Tasks: Code Evidence Index (SharpAriadne)

**Feature**: `002-code-evidence-index` | **Scope**: M0–M3 (M4/M5 are later work and are not listed)
**Legend**: every task names its files, what to build, and the test that proves it done. A task is done
only when `dotnet build SharpCoreAgent.slnx -c Release` and
`dotnet test tests/SharpCoreAgent.Tests/SharpCoreAgent.Tests.csproj -c Release` pass with zero warnings.

## Phase 0 — Foundations (M0)

- [x] **T001** Wire the SharpCoreDB dependency
  - Files: `Directory.Packages.props`, `nuget.config`, `src/SharpCoreAgent.Indexing/SharpCoreAgent.Indexing.csproj`, `SharpCoreAgent.slnx`
  - Do: add `PackageVersion` rows at `2.1.0-RC.3` for `SharpCoreDB`, `SharpCoreDB.Search`,
    `SharpCoreDB.VectorSearch`, `SharpCoreDB.Graph`, `SharpCoreDB.Graph.Advanced` and
    `SharpCoreDB.HybridSearch`, sourced from nuget.org like every other dependency (a `sharpcoredb-local`
    folder source was tried first, while the packages were unpublished, and removed when they shipped);
    create the project with `net11.0`, nullable enabled, references to `SharpCoreAgent.Domain` and those
    six packages.
  - Done when: `dotnet restore` resolves all six from nuget.org with no `NU1101`.

- [x] **T002** Add the project-reference escape hatch
  - Files: `src/SharpCoreAgent.Indexing/SharpCoreAgent.Indexing.csproj`
  - Do: `SharpCoreAgentUseLocalSharpCoreDb=true` switches six `PackageReference`s to `ProjectReference`s into
    `..\..\..\SharpCoreDB\src\*`; default false. (The conditional `Error` that explained a missing local
    feed went with the feed: an absent nuget.org package is an ordinary `NU1101`.)
  - Done when: both switches restore and build; the package path is the default.

- [x] **T003** Write the Domain surface
  - Files: `src/SharpCoreAgent.Domain/Index/*.cs` (enums, `CodeQuery`, `CodeEvidence`, `CodeSymbolRef`,
    `IndexStatus`, `IndexBuildReport`, `IndexVerification`, `ICodeIndex`, `ICodeIndexMaintainer`),
    `src/SharpCoreAgent.Domain/PublicAPI.Unshipped.txt`
  - Do: implement `data-model.md` §1–§4 exactly, including `CodeQuery.Create` validation (empty text,
    `K` 1–12, `Hops` 0–3, closed `Kind` list, `/` normalisation).
  - Test: `CodeQueryTests` — every rejected input, every clamp, every normalisation.
  - Done when: the API snapshot is updated and RS0016/RS0017 pass.

- [x] **T004** Create the schema
  - Files: `src/SharpCoreAgent.Indexing/IndexSchema.cs`, `IndexPaths.cs` (`SharpCoreDbIndexDatabase.cs` is the shared opener the later tasks grow)
  - Do: the DDL from `contracts/index-schema.md` verbatim, in one transaction, idempotent; seed `meta`;
    resolve `index/`, `.lock` and the database under `Workspace.Root`.
  - Test: `IndexSchemaTests` — all seven tables exist, `meta` has the fifteen keys from the contract, running
    twice is a no-op, `dimensions` matches the `VECTOR(n)` declaration, `edges` has `source`/`target`/`weight`.

- [x] **T005** Open the index read-only
  - Files: `src/SharpCoreAgent.Indexing/SharpCoreDbCodeIndex.cs`
  - Do: open with `NoEncryptMode` (unless configured), read `meta`, compare `schema_version` /
    `analyzer_contract` / `workspace_root`, and report `Missing`, `Corrupt`, `Stale` or `Fresh`; cache the
    status per process; never throw for absence.
  - Test: `CodeIndexOpenTests` — missing directory ⇒ `Missing`; foreign root ⇒ `Corrupt`; newer schema ⇒
    `Corrupt`; a second open reuses the handle; orphaned rows are reported, not thrown.

- [x] **T006** Single writer, many readers
  - Files: `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs`
  - Do: `.lock` with `FileShare.None` and a timed wait; `IndexFreshness.Building` when the wait fails;
    release in `DisposeAsync`; one build id (ULID) per run.
  - Test: `WriterLockTests` — two maintainers, the second reports `Building`; a stale lock file does not
    deadlock the next run.

## Phase 1 — Extract and store (M1)

- [x] **T007** Scan the workspace
  - Files: `src/SharpCoreAgent.Indexing/Extraction/WorkspaceFileScanner.cs`
  - Do: walk with the `IgnoredPaths` list, skip whatever `TextLimits.IsSearchableText` already refuses
    (>1 MiB, NUL bytes in the head), deduplicate junctions by real path, order ordinally, yield
    `(path, size, mtime, language, skipReason)`.
  - Test: `WorkspaceFileScannerTests` — ignores `.git`/`node_modules`/`bin`/`obj`/`.vs`/`.sharpcoreagent`,
    reports `skipped` with a reason, ordinal order is stable across runs.

- [x] **T008** Tokenise code
  - Files: `src/SharpCoreAgent.Indexing/Extraction/CodeTokenizer.cs`
  - Do: split identifiers at camelCase, PascalCase, snake_case, dots and digits; lowercase; drop tokens
    shorter than 2 characters and punctuation; expose `TokenizeForIndex` (space-joined) and
    `TokenizeForQuery`.
  - Test: `CodeTokenizerTests` — `RunTurnAsync` ⇒ `run turn async`, `IModelClient` ⇒ `i model client`,
    `turn_async` ⇒ `turn async`, output stable across calls.

- [x] **T009** Embed locally
  - Files: `src/SharpCoreAgent.Indexing/Embedding/ICodeTextEmbedder.cs`, `Embedding/HashedCodeEmbedder.cs`
  - Do: FNV-1a per token into 512 dimensions, `1 + log(tf)` weight, L2-normalise, `Id = "hashed-local/512"`.
  - Test: `HashedCodeEmbedderTests` — determinism across instances, norm ≈ 1, unrelated texts have
    cosine < 0.2, empty text yields the zero vector without throwing.

- [x] **T010** Extract facts heuristically
  - Files: `src/SharpCoreAgent.Indexing/Extraction/ISyntaxFactsExtractor.cs`, `Extraction/HeuristicSyntaxFactsExtractor.cs`
  - Do: namespace/type/member declarations with signatures and line ranges, XML-doc summaries, call
    sites, inherited/implemented types, `using`/`import`, and doc mentions; ignore comments and strings
    when detecting calls; produce a stable `key`.
  - Test: `HeuristicSyntaxFactsExtractorTests` on the fixture corpus: expected declaration counts, one
    `inherits` edge, one `implements`, `calls` weights capped at 5, no edge from a comment.

- [x] **T011** Chunk into passages
  - Files: `src/SharpCoreAgent.Indexing/Extraction/PassageChunker.cs`
  - Do: declaration-aligned splits, then blank-line blocks, then hard line splits; ≤40 lines and ≤1 600
    characters; no overlap; 1-based inclusive ranges; attach the declaring symbol when a split starts a
    declaration.
  - Test: `PassageChunkerTests` — a 4 000-line generated block is chunked, not truncated; no passage
    exceeds either cap; no passage starts or ends mid-line; ranges tile the file without gaps.

- [x] **T012** Write one file as one transaction
  - Files: `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs` (write path)
  - Do: skip when size+mtime match and the hash confirms; otherwise delete the file's rows and insert
    chunks/symbols/edges/vectors/docs; recompute `chunks.tokens`; record `state`, counts and `indexed_utc`;
    prune files that disappeared; update `meta` counters and `build_id`/`build_utc` last.
  - Test: `IncrementalIndexTests` — add/modify/delete one file in a five-file workspace and assert exact
    counts, `0 changed` on a repeat run, and the previous `build_id` surviving a simulated mid-file failure.

- [ ] **T013** Assign communities
  - Files: `src/SharpCoreAgent.Indexing/Graph/CommunityAssigner.cs`
  - Do: load the build's edges into `Graph.Advanced`, run Louvain once, write `symbols.community` as
    `c<n>` plus degree centrality; deterministic ordering of communities by first member key.
  - Test: `CommunityAssignerTests` — two disjoint clusters get two distinct labels; the same corpus
    produces the same labels twice.

- [x] **T014** Refresh within a budget
  - Files: `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs` (`EnsureFreshAsync`, `RebuildAsync`)
  - Do: `EnsureFreshAsync(TimeSpan)` builds or refreshes until the budget expires, then returns
    `Incomplete = true` with `build_state = partial`; `RebuildAsync` clears the tables first; both emit
    `Notes` for skipped files.
  - Test: `RefreshBudgetTests` — a budget of 1 ms on a 50-file corpus returns `Incomplete` and leaves a
    usable index; a second call finishes it; a no-op refresh does no writes.

- [x] **T015** Verify and prune
  - Files: `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs` (`VerifyAsync`, `PruneAsync`)
  - Do: compare `files` with the scanner (missing both ways), re-hash stored passages, count orphans, and
    answer `IsHealthy`; `PruneAsync` deletes only rows whose file is gone.
  - Test: `VerifyPruneTests` — a deleted file shows up as `FilesMissingOnDisk` and is removed by prune; a
    hand-edited `chunks.text` shows up as a hash mismatch; a clean index reports `IsHealthy`.

## Phase 2 — Retrieval and `search_code` (M2)

- [x] **T016** Load the reader
  - Files: `src/SharpCoreAgent.Indexing/Retrieval/IndexReader.cs`
  - Do: on first query, open the database, load `chunks.tokens` into SharpCoreDB's `FullTextIndex`
    (already-tokenised, no re-analysis), snapshot `edges` into the adjacency map, cache chunk count and
    embedder id; reopen when `build_id` changes.
  - Test: `IndexReaderTests` — the snapshot matches the table counts, a rebuild invalidates it, and a
    second query does no I/O against `chunks`.

- [x] **T017** Lexical leg
  - Files: `src/SharpCoreAgent.Indexing/Retrieval/LexicalRetriever.cs`
  - Do: BM25 over the in-memory index, `TopN = 4 × K`, tokenised query, `LIKE` fallback when the in-memory
    index is unavailable; return `(chunkId, path, startLine, endLine, score)` ordered deterministically.
  - Test: `LexicalRetrieverTests` — an exact identifier ranks its declaration first; a stop-word-only
    query returns nothing; scores are non-increasing.
  - Note: the leg finds the declaring passage and returns it as a candidate; putting it *first* is the
    exact-symbol boost of T019, because BM25 alone weighs a short abstract declaration above the longer file
    that also mentions the identifier. The `LIKE` fallback is not built: the snapshot loads every passage, so
    there is no state in which the in-memory index is unavailable while the rows are readable.

- [x] **T018** Vector leg
  - Files: `src/SharpCoreAgent.Indexing/Retrieval/VectorRetriever.cs`
  - Do: embed the query once, `ORDER BY vec_distance_cosine(embedding, ?)`, `LIMIT 4 × K`; return nothing
    when the embedder is off or dimensions mismatch (with a note, not an exception).
  - Test: `VectorRetrieverTests` — a paraphrase with no shared token still finds the passage; `--embedder
    off` short-circuits; a dimension mismatch produces a note.
  - Done: the leg returns `VectorLegResult` (matches + note), `4 × K` candidates, a 0.05 similarity floor,
    ties broken by passage id. Three deviations from the plan, each forced by 2.1.0-RC.3 and recorded in
    `research.md`: the SQL route is unusable (the vector query optimizer is a stub that returns `null`, a
    projection containing a function is ignored and returns whole rows, and `WHERE vec_distance_cosine(...)`
    matches nothing), so the cosine runs over the vectors the snapshot holds; the writer's `float[]`
    parameter was being stored as the literal text `System.Single[]`, so vectors are now written as JSON
    (`VectorSerializer.ToJson`) and `analyzer_contract` went to 2 to make older indexes rebuild; and a
    passage is now hashed over its trimmed text, because the engine does not round-trip surrounding
    whitespace. The test that promised "a paraphrase with no shared token" was replaced by one that asserts
    what a hashed bag of words can actually promise: the passage about the question, ordered by cosine.
    Two of those three deviations were retired by the engine bump to `2.1.1` (2026-10-25): the writer binds
    the embedder's `float[]` again instead of encoding it as JSON, and `analyzer_contract` is 3 so the cells
    the old path wrote are re-derived. The C# cosine stays: the repaired planner engages only on a
    directory-mode database, and this index is one file (`research.md`, D6 re-measurement).

- [x] **T019** Fuse and boost
  - Files: `src/SharpCoreAgent.Indexing/Ranking/ReciprocalRankFusion.cs`, `Ranking/EvidenceRanker.cs`
  - Do: RRF with k=60, then the boosts from `research.md` (declaration match, path match, recency,
    same-file neighbour of a graph hit, doc penalty), then the 0.5/0.3/0.2 lexical/vector/graph terms;
    sort by `(score, path, start_line)`; merge adjacent passages of one file when they touch.
  - Test: `EvidenceRankerTests` — a document unique to one leg still ranks; equal scores break by path;
    the same input yields the same order ten times.
  - Done: `ReciprocalRankFusionTests` and `EvidenceRankerTests` cover the fusion, the boosts, the graph
    term, the path and kind filters, the stale flag and determinism. Two notes. First, the chunker had to be
    fixed before the exact-symbol boost could ever fire: it closed a passage at the *last* boundary inside
    the 40-line window, so a small class became one passage named after its first member. It now ends at the
    next declaration, which is what `research.md` D4 always said. Second, a type's own passage holds its
    header, so `kind=class` combined with a query about what the type *does* returns nothing: the filter
    narrows, it does not aggregate members up to their type.

- [x] **T020** Respect the budget
  - Files: `src/SharpCoreAgent.Indexing/Ranking/EvidenceBudget.cs`
  - Do: ≤12 blocks, ≤2 per file, ≤1 600 characters per excerpt, whole result under
    `MaxToolResultChars`; when trimming, drop the lowest-scoring block and say so in the header.
  - Test: `EvidenceBudgetTests` — a 40-block candidate list yields 12; a file with 9 hits contributes 2;
    the rendered string never exceeds the cap.
  - Done: `BudgetedEvidence` carries the counts (`DroppedByFileCap`, `DroppedByCount`, `DroppedBySize`,
    `Trimmed`) and a `Note` the tool renders in its header. The character cap is measured with a per-block
    estimate (`OverheadChars` + heading + excerpt), not by rendering here: formatting belongs to the tool.

- [ ] **T021** Ship `search_code`
  - Files: `src/SharpCoreAgent.Infrastructure/Tools/SearchCodeTool.cs`
  - Do: the exact schema and output from `contracts/retrieval-tools.md`; JSON-string `arguments`;
    `ToolResult.IsError` for an unusable query, `Failure` for a missing index; no exception escapes.
  - Test: `SearchCodeToolTests` — schema snapshot, malicious input, `k` clamped, no-match message, missing
    index message, output format including `sha256:` and `why:`.

- [ ] **T022** Prove retrieval quality
  - Files: `tests/SharpCoreAgent.Tests/Indexing/Fixtures/Corpus/*`, `RetrievalQualityTests.cs`
  - Do: a 24-file fixture corpus with 12 seeded questions and a CI-scaled version of SC-001 — top 3 for
    ≥10 of 12, first for ≥8 (a small corpus makes top-five trivially easy, so the test asks for top three);
    the 200-file/25-query benchmark itself is a manual run recorded in the PR, like the performance smoke in
    `plan.md`.
  - Test: `RetrievalQualityTests` (SC-001, CI-scaled) — measured recall printed on failure.

## Phase 3 — Symbols, context, lifecycle (M3)

- [ ] **T023** Ship `find_symbol`
  - Files: `src/SharpCoreAgent.Indexing/Retrieval/SymbolRetriever.cs`, `src/SharpCoreAgent.Infrastructure/Tools/FindSymbolTool.cs`
  - Do: exact `key`, then `name COLLATE NOCASE`, then infix `LIKE`; `Kind` filter; cap 50; declaration
    line range and signature from `symbols`.
  - Test: `FindSymbolToolTests` — exact beats nocase beats infix; empty name rejected; kind filter
    narrows; no match says so; capped at 50 with a note.

- [ ] **T024** Ship `find_references`
  - Files: `src/SharpCoreAgent.Indexing/Retrieval/ReferenceRetriever.cs`, `src/SharpCoreAgent.Infrastructure/Tools/FindReferencesTool.cs`
  - Do: outgoing edges and incoming edges read from `edges` (so `called_by` is a reverse read), group by
    `kind` and file, `MaxFiles` cap with a `+N more` note.
  - Test: `FindReferencesToolTests` — a caller appears under `called_by`; the callee does not appear under
    `calls`; unknown symbol says so.

- [ ] **T025** Ship `code_context`
  - Files: `src/SharpCoreAgent.Indexing/Retrieval/NeighbourhoodRetriever.cs`, `src/SharpCoreAgent.Infrastructure/Tools/CodeContextTool.cs`
  - Do: BFS over `edges` to `Hops`, weighted ordering, `Direction` filter, community peers, declaration
    excerpt for the focus symbol; reuse `EvidenceBudget` rendering.
  - Test: `CodeContextToolTests` — hops=1 vs hops=2 differ; direction filters; `hops` clamped to 0–3;
    relationship labels are the closed set.

- [ ] **T026** Ship `index_status`
  - Files: `src/SharpCoreAgent.Infrastructure/Tools/IndexStatusTool.cs`
  - Do: the one-line status from `ICodeIndex.GetStatusAsync`, plus the coverage lines from
    `contracts/retrieval-tools.md`; never builds.
  - Test: `IndexStatusToolTests` — missing index, fresh index, partial index (three fixtures).

- [ ] **T027** Compose the service collection
  - Files: `src/SharpCoreAgent.DependencyInjection/IndexingServiceCollectionExtensions.cs`,
    `src/SharpCoreAgent.DependencyInjection/IndexingOptions.cs`, `ServiceCollectionExtensions.cs`
  - Do: `AddSharpCoreAgentIndex` registers options binding (configuration section `Indexing`), `ICodeIndex`, the
    maintainer, the retrievers and the five tools behind `IndexingOptions.Enabled`; the composition root
    calls it.
  - Test: `IndexingRegistrationTests` — five tools resolve; `Enabled = false` registers nothing and calls
    fall back to "disabled"; options bind from configuration.

- [ ] **T028** Parse the new commands
  - Files: `src/SharpCoreAgent.Cli/CliOptions.cs`
  - Do: `CliCommand.Index`, `--status`/`--rebuild`/`--verify`/`--prune`, `--embedder`, `--tier`,
    `--no-index`, `--index-budget`; the conflict and range rules from `contracts/cli-index.md`; a leading
    `ariadne` token is accepted and dropped so a shell alias works.
  - Test: `CliOptionsTests` — every parsing rule in that contract, including each error message.

- [ ] **T029** Execute `index`
  - Files: `src/SharpCoreAgent.Cli/Commands/IndexCommand.cs`, `Program.cs`, `HelpText.cs`, `ExitCodes.cs`
  - Do: status/build/rebuild/verify/prune, text and `--json` output from the contract, exit codes 0/1/2/3/
    4/6, and `ExitCodes.IndexUnavailable = 6` next to the existing constants.
  - Test: `IndexCommandTests` — status on missing/fresh/partial, verify drift ⇒ exit 1, unwritable
    `.sharpcoreagent` ⇒ exit 6, `--json` shape snapshot.

- [ ] **T030** Refresh before the turn
  - Files: `src/SharpCoreAgent.Cli/Program.cs`, `src/SharpCoreAgent.Cli/CliHost.cs`,
    `src/SharpCoreAgent.Infrastructure/Tools/WriteFileTool.cs`, `Tools/DeleteFileTool.cs`
  - Do: `EnsureFreshAsync(--index-budget)` before the first model call; mark written/deleted paths dirty;
    refresh before the next turn (chat) or after the turn (run); skip everything under `--no-index`; print
    one line unless `--quiet`.
  - Test: `TurnStartRefreshTests` — a stale file is refreshed before the call; `--no-index` creates
    nothing; a write invalidates its file for the next turn.

- [ ] **T031** Tell the model to search first
  - Files: `src/SharpCoreAgent.Application/PromptLibrary.cs`
  - Do: one rule naming the five tools and the "cite `path:start-end`" obligation, emitted only when the
    index is enabled and present.
  - Test: `PromptLibraryTests` — the rule appears with the index, is absent with `--no-index`, and names
    every tool.

- [ ] **T032** Name the build
  - Files: `src/SharpCoreAgent.Cli/HelpText.cs`
  - Do: `--version` prints `Ariadne <version> (SharpCoreAgent)`; the usage block gains the `index` row and the
    exit-code 6 row.
  - Test: `HelpTextTests` — version format, `index` documented, exit 6 documented.

- [ ] **T033** End-to-end regression
  - Files: `tests/SharpCoreAgent.Tests/Indexing/EndToEndIndexTests.cs`
  - Do: in a temp workspace — build, query all five tools, edit a file, refresh, verify, prune, rebuild —
    then assert counts, citations and exit codes.
  - Test: `EndToEndIndexTests` — the quickstart, scripted.

## Traceability — requirement to task

| FR | Tasks | FR | Tasks |
|---|---|---|---|
| FR-001 | T004 T005 | FR-016 | T024 |
| FR-002 | T007 T012 | FR-017 | T025 |
| FR-003 | T011 T012 | FR-018 | T021 T023–T026 |
| FR-004 | T010 T012 | FR-019 | T021 T026 T027 |
| FR-005 | T010 T012 | FR-020 | T028 T029 |
| FR-006 | T009 T018 | FR-021 | T014 T030 |
| FR-007 | T004 T005 | FR-022 | T030 |
| FR-008 | T007 | FR-023 | T026 |
| FR-009 | T012 | FR-024 | T006 |
| FR-010 | T005 T014 | FR-025 | T028 T030 |
| FR-011 | T017 T018 T019 | FR-026 | T009 T018 |
| FR-012 | T019 T021 | FR-027 | T005 |
| FR-013 | T019 T021 | FR-028 | T007 T021 |
| FR-014 | T020 T021 | FR-029 | T007 T012 |
| FR-015 | T023 | FR-030 | T031 |

## Dependencies

- T001 blocks everything; T002 is independent of T001 once the versions exist.
- T003 blocks T004–T033 (the ports are the contract).
- T004 blocks T005; T005 blocks T012, T016.
- T007–T011 block T012; T012 blocks T013–T015, T016.
- T016 blocks T017–T019; T019 blocks T020; T020/T021 block T022.
- T016–T021 block T023–T026; T026 blocks T033.
- T027 blocks T021, T023–T026 (tool registration) and T030.
- T028 blocks T029; T029 blocks T030 (the command shares `IndexPaths` and the maintainer).
- T032 is independent of every other task.

## Suggested order for one developer

M0 in one sitting (T001–T006), then M1 in order (T007 → T015), then M2 (T016 → T022), then M3 by
dependency: T023–T027, then T028 → T029 → T030 → T031, then T032 and T033.

## Parallelisable pairs

- T007 ‖ T008 ‖ T009 ‖ T010 ‖ T011 — five independent pure functions over the same fixture corpus.
- T017 ‖ T018 — two retrieval legs behind the same interface.
- T023 ‖ T024 ‖ T025 ‖ T026 — four tools over one reader, once T027's registration exists.
- T032 ‖ anything.

## Definition of done (per task, and for the milestone)

1. The code builds with `-c Release` and zero warnings (`TreatWarningsAsErrors` is on).
2. The named test exists and fails before the code, passes after.
3. New `public` members appear in `PublicAPI.Unshipped.txt` (Domain only) with the right
   `static`/`sealed`/`nullable` annotations.
4. XML documentation on every public member; `/// <summary>` says what it is for, not what it does.
5. No `catch (Exception)` that swallows; no `async void`; no `.Result`/`.Wait()`.
6. The task's acceptance sentence from this file is true when run by hand.

## Not in this list

M4 (Roslyn-backed `ISyntaxFactsExtractor`, generics and partial-class accuracy) and M5 (provider
embeddings, richer quantisation, HNSW tuning at scale) are deliberately excluded: both swap an
implementation behind an interface that T010/T009 create, so they need no schema change and no new task
ordering. `plan.md` §Milestones records the same boundary.





