# Implementation Plan: Code Evidence Index (SharpAriadne)

**Branch**: `002-code-evidence-index` | **Date**: 2026-10-24 | **Spec**: [spec.md](./spec.md)

**Input**: `specs/002-code-evidence-index/spec.md`

## Summary

Add a per-workspace, persistent code-evidence index to SharpAriadne. A new `SharpCoreAgent.Indexing`
project extracts files, declaration-aligned passages, symbols and typed edges into a SharpCoreDB
database under `.sharpcoreagent/index/`, stores a hashed local embedding per passage and per symbol, and
answers five read-only tool calls (`search_code`, `find_symbol`, `find_references`, `code_context`,
`index_status`) through an `ICodeIndex` port declared in `SharpCoreAgent.Domain`. The CLI gains
`sharpcoreagent index [--status|--rebuild|--verify|--prune]` and a `--no-index` switch; `run`/`chat` refresh
the index before the first model call within a three-second budget.

Scope of this plan: **M0–M3** (ports and schema, extract/embed/store, retrieval and the five tools,
lifecycle and CLI). Roslyn extraction (M4) and a provider embedder (M5) are designed for but not built.

## Technical Context

**Language/Version**: C# 15 (`LangVersion` `preview`), `net11.0`, runtime-async on — the repository's
existing settings, inherited from `src/Directory.Build.props`.

**Primary dependencies**: BCL plus, new in this feature, `SharpCoreDB`, `SharpCoreDB.Search`,
`SharpCoreDB.VectorSearch`, `SharpCoreDB.Graph`, `SharpCoreDB.Graph.Advanced`,
`SharpCoreDB.HybridSearch` — all `2.1.0-RC.3`, all `net11.0`, all from nuget.org like every other
dependency (see `research.md` D1 and Complexity Tracking below).

**Storage**: one SharpCoreDB database per workspace at `<workspace>/.sharpcoreagent/index/sharpcoreagent.scdb`,
unencrypted by default (`NoEncryptMode`), opt-in encrypted.

**Testing**: xUnit v3 on the Microsoft.Testing.Platform, in `tests/SharpCoreAgent.Tests`, using temp
directories per test (`ToolTestWorkspace` pattern) and a tiny fixture corpus committed under
`tests/SharpCoreAgent.Tests/Indexing/Corpus/`.

**Target platform**: the same desktop platforms the CLI already targets — no native binaries, no
platform-specific code.

**Project type**: a sixth class library plus edits to five existing projects.

**Performance goals**: cold index of 5 000 files < 90 s; incremental one-file run < 3 s; `search_code`
p95 < 250 ms on 20 000 passages; turn-start refresh ≤ 3 s of wall clock (spec SC-002, SC-003, FR-021).

**Constraints**: offline by default (FR-026); deterministic ranking (FR-013, SC-007); no writes outside
`.sharpcoreagent/`; all tools `ReadOnly`; zero build warnings (Principle VII).

**Scale/scope**: workspaces up to ~50 000 files / 200 000 passages; `MaxChunks` bounds the in-memory
lexical index; the graph snapshot is `O(edges)` and rebuilt on open.

## Constitution Check

*Gate: every principle must be satisfied or explicitly justified.*

| # | Principle | How this plan satisfies it |
|---|---|---|
| I | The Model Is a Port, Never a Dependency | No model is called by the index. The optional provider embedder (M5) sits behind `ICodeTextEmbedder` and is off by default, so no provider type enters the index path. |
| II | Tools Are the Only Door to the Machine | All five index tools take `ToolInvocationContext` and go through `Workspace` for any path filter; the index path is derived from `Workspace.Root`, never from model input. The loop never opens the database. |
| III | Untrusted Output, Validated at the Boundary | Query text reaches SQL only as a parameter (`@name`); `k`, `hops` and `direction` are range-checked before use; anything read from the index is re-validated as workspace-relative before it reaches a tool result. |
| IV | Every Loop Has a Budget | Every tool caps its own output (blocks, lines, characters); the refresh has a wall-clock budget and degrades to `partial`; the in-memory index has a hard chunk ceiling. |
| V | Streaming Is the Default | Unchanged: the index adds no streaming surface; tool results are bounded single payloads like every other tool. |
| VI | Evidence over Assertion | This is the principle the feature implements: every block carries `path:start-end`, a content hash and the legs that produced it, and stale evidence is labelled instead of presented as current. |
| VII | Zero Warnings, Snapshot Surface | New Domain types are added to `PublicAPI.Unshipped.txt` (RS0016 is an error); Release stays warning-free; every task closes with build + tests. |
| VIII | Secrets Stay in the Process | The index stores no credential and no transcript; `IgnoredPaths` already excludes `.sharpcoreagent`; the only credential this feature can touch is the opt-in encryption key, read from the environment and never written. |

**Result**: PASS. Two deviations are recorded below: a sixth project and a third-party dependency.

## Complexity Tracking

| Deviation | Why it is necessary | What was rejected, and why |
|---|---|---|
| New project `src/SharpCoreAgent.Indexing` (the constitution names five layers) | It is the only assembly that references SharpCoreDB. Putting the engine in `SharpCoreAgent.Infrastructure` would compile the graph/vector stack into every CLI build and every tool test, and would erase the "one place that knows the storage engine" boundary. | *Engine inside Infrastructure* — rejected for dependency weight and the lost boundary. *Engine inside the CLI* — rejected because the composition root must register it. |
| Six SharpCoreDB packages at `2.1.0-RC.3` | A graph + BM25 + vector + community engine from scratch is a multi-month project, and the organisation already ships exactly this one. The BCL cannot satisfy it. | *SQLite + fts5* — no graph, no vectors. *Lucene.NET* — large, still no graph. *Hand-rolled* — out of scope. |
| A local folder feed in `nuget.config` | `2.1.0-RC.3` is not on nuget.org; the sibling `artifacts` folder is a valid feed and keeps the build reproducible. | *Publishing from this feature* — release engineering, not a feature. *Vendoring sources* — two copies of the engine. |
| `--embedder provider` (opt-in, M5) | Keeps the door open for better recall without making the default path networked. | *Provider by default* — breaks FR-026 and SC-008. |
| `symbols.community` and `symbols.centrality` columns | Communities are a whole-graph computation; storing them turns a per-query Louvain run into a column read (research D10). | *Compute per query* — seconds per tool call. *No community signal* — loses the 0.2 term of the GraphRAG shape. |

## Project Structure

### Documentation (this feature)

```
specs/002-code-evidence-index/
├── spec.md                      product behaviour, FR-001…FR-030, SC-001…SC-008
├── plan.md                      this file
├── research.md                  D1…D15, the decisions behind every constant
├── data-model.md                Domain records, port signatures, table-by-table storage model
├── quickstart.md                the five commands a reviewer runs first
├── contracts/
│   ├── index-schema.md          DDL, invariants, meta keys, migration rule
│   ├── retrieval-tools.md       the five tools: JSON schemas, output format, failure modes
│   └── cli-index.md             `sharpcoreagent index`, `--no-index`, slash commands, exit codes
└── tasks.md                     M0–M3 task list, each with its acceptance test
```

### Source tree (new files in **bold**)

```
SharpCoreAgent.slnx                                  + SharpCoreAgent.Indexing project entry
Directory.Packages.props                          + six SharpCoreDB PackageVersion rows
nuget.config                                      + local folder feed + packageSourceMapping
src/
├── SharpCoreAgent.Domain/
│   ├── Index/                                    **new**
│   │   ├── ICodeIndex.cs                        **the read port (5 tools bind to this)**
│   │   ├── ICodeIndexMaintainer.cs              **build/refresh/verify/prune port (host only)**
│   │   ├── CodeEvidence.cs                      **one citable block**
│   │   ├── CodeSymbolRef.cs                     **one declaration**
│   │   ├── CodeQuery.cs                         **request value**
│   │   ├── IndexStatus.cs                       **coverage + freshness**
│   │   ├── IndexBuildReport.cs                  **what a build changed**
│   │   ├── IndexVerification.cs                 **drift report**
│   │   └── IndexEnums.cs                        **IndexTier, EvidenceLeg, IndexFreshness, EmbedderKind, ReferenceDirection, VectorQuantization**
│   ├── Workspace.cs                             unchanged (the index path is derived from Root)
│   └── PublicAPI.Unshipped.txt                  + every new public member
├── SharpCoreAgent.Indexing/                          **new project**
│   ├── SharpCoreAgent.Indexing.csproj               **SharpCoreDB refs + switch**
│   ├── IndexSchema.cs                           **DDL strings, PRAGMA/index creation, schema version**
│   ├── IndexPaths.cs                            **database, lock and temp paths under .sharpcoreagent/index**
│   ├── SharpCoreDbCodeIndex.cs                  **ICodeIndex over an open SharpCoreDB database**
│   ├── SharpCoreDbIndexMaintainer.cs            **ICodeIndexMaintainer: scan, extract, embed, store**
│   ├── Retrieval/                                **per-index snapshot + the three legs + the tools' queries**
│   │   ├── IndexReader.cs                       **BM25 + vector + adjacency snapshot per open index**
│   │   ├── LexicalRetriever.cs                  **BM25 over the in-memory index, LIKE fallback**
│   │   ├── VectorRetriever.cs                   **`vec_distance_cosine`, skipped when the embedder is off**
│   │   ├── SymbolRetriever.cs                   **exact → nocase → infix, kind filter**
│   │   ├── ReferenceRetriever.cs                **outgoing + incoming edges, grouped per file**
│   │   └── NeighbourhoodRetriever.cs            **weighted BFS to `hops`, community peers**
│   ├── Ranking/                                  **fusion, boosts, budget selection**
│   │   ├── ReciprocalRankFusion.cs
│   │   ├── EvidenceRanker.cs                    **RRF + boosts + 0.5/0.3/0.2 terms**
│   │   └── EvidenceBudget.cs
│   ├── Extraction/
│   │   ├── ISyntaxFactsExtractor.cs             **the M4 seam (Roslyn lands here)**
│   │   ├── HeuristicSyntaxFactsExtractor.cs     **braces + declaration regex, language-agnostic**
│   │   ├── CodeTokenizer.cs                     **camelCase/snake_case/dot splitting, FNV-1a tokens**
│   │   ├── PassageChunker.cs                    **declaration-aligned, ≤40 lines / ≤1600 chars**
│   │   └── WorkspaceFileScanner.cs              **IgnoredPaths + size + binary rules, ordinal order**
│   ├── Embedding/
│   │   ├── ICodeTextEmbedder.cs                 **port: id, dimensions, Embed**
│   │   └── HashedCodeEmbedder.cs                **512-d FNV-1a, deterministic**
│   └── Graph/
│       ├── AdjacencySnapshot.cs                 **forward/reverse maps + weighted BFS**
│       └── CommunityAssigner.cs                 **Louvain at build time → symbols.community**
├── SharpCoreAgent.Infrastructure/Tools/              + 5 tool classes, no SharpCoreDB reference
│   ├── SearchCodeTool.cs
│   ├── FindSymbolTool.cs
│   ├── FindReferencesTool.cs
│   ├── CodeContextTool.cs
│   └── IndexStatusTool.cs
├── SharpCoreAgent.Application/PromptLibrary.cs       + "search the index first" rule (FR-030)
├── SharpCoreAgent.DependencyInjection/
│   ├── IndexingOptions.cs                        **tier, embedder, dimensions, budget, encryption**
│   ├── ServiceCollectionExtensions.cs            + AddSharpCoreAgentIndex + 5 tool registrations
│   └── SharpCoreAgent.DependencyInjection.csproj     + project reference to SharpCoreAgent.Indexing
└── SharpCoreAgent.Cli/
    ├── CliOptions.cs                             + `index` command, index switches, --no-index,
    │                                               leading `ariadne` alias (spec §Naming)
    ├── HelpText.cs                               + options, `index` usage, exit code 6,
    │                                               `Ariadne <version> (SharpCoreAgent)` version line
    ├── ExitCodes.cs                              + IndexUnavailable = 6
    └── Commands/IndexCommand.cs                  **new**
tests/SharpCoreAgent.Tests/Indexing/                  **new** (schema, extraction, embedding, ranking, tools, freshness)
```

## Milestones

| Milestone | Delivers | Exit criterion |
|---|---|---|
| **M0** Foundations | Six packages wired; `SharpCoreAgent.Indexing` project; Domain `Index/` ports + records; `IndexSchema`; `SharpCoreDbCodeIndex` open/close/status; reader lock | `dotnet build -c Release` and `dotnet test -c Release` green; a temp database opens, reports `Missing` then `Fresh`, and survives a re-open |
| **M1** Extract and store | File scanner, passage chunker, code tokenizer, heuristic extractor, hashed embedder, per-file transaction writer, community assignment | Cold rebuild of the fixture corpus yields the expected file/chunk/symbol/edge counts; a second run reports `0 changed`; one edited file reports `1 changed` |
| **M2** Retrieve | BM25 leg, vector leg, RRF fusion, graph expansion, boosts, budget selection, `search_code` tool | SC-001 met on the fixture corpus; identical results across two runs; a result stays under `MaxToolResultChars` |
| **M3** Symbols, context, lifecycle | `find_symbol`, `find_references`, `code_context`, `index_status`; `sharpcoreagent index` with all flags; `EnsureFreshAsync` on `run`/`chat`; `--no-index`; prompt rule; `Ariadne` version string and the `ariadne` alias | Every FR in M3 has at least one test; `quickstart.md` runs end to end on the SharpCoreAgent repo itself |
| **M4** (later) | Roslyn extractor behind `ISyntaxFactsExtractor`; generics/partial-class accuracy | Not in this feature's task list |
| **M5** (later) | Provider embedder, quantisation options beyond int8, HNSW tuning at scale | Not in this feature's task list |

## Testing Strategy

- **Unit**: `PassageChunker` (boundaries, 40-line and 1 600-char caps, never mid-line), `CodeTokenizer`
  (camelCase/PascalCase/snake_case/dot), `HashedCodeEmbedder` (same input → same vector; L2 norm ≈ 1;
  different input → different vector), `ReciprocalRankFusion` (rank arithmetic, stable tie-break),
  `EvidenceBudget` (per-file cap, character cap, truncation flag), `AdjacencySnapshot` (BFS depth,
  reverse edges).
- **Integration**: `IndexSchema` on a temp database (tables exist, `meta` seeded, second `Create` is a
  no-op), incremental rebuild (add/modify/delete a file and assert counts), stale detection,
  `VerifyAsync` drift, corrupt-file recovery, writer-lock contention.
- **Tool contract**: each of the five tools asserted against the exact JSON schema string, the
  `ReadOnly` safety, the `Read` effect, the output shape (`path:start-end`, `sha8`, `why`), the empty-index
  error result and the truncation marker.
- **End-to-end**: `sharpcoreagent index` on a temp workspace through `CliOptions.Parse` + `IndexCommand`;
  `--json` shape; exit code 6 when `.sharpcoreagent` is unwritable.
- **Performance smoke** (not in CI): a 5 000-file synthetic tree for SC-002/SC-003, run manually before
  a release and recorded in the PR.

## Quality Gates

Every task closes with:

```
dotnet build SharpCoreAgent.slnx -c Release
dotnet test tests/SharpCoreAgent.Tests/SharpCoreAgent.Tests.csproj -c Release
```

Zero warnings (Principle VII), all tests passing, and `PublicAPI.Unshipped.txt` updated in the same
commit as any new Domain member (RS0016 fails the build otherwise).

## Out of Scope (documented, not forgotten)

MCP server · index-write tools · git-history indexing · cross-workspace/shared index · compiler-grade
symbol resolution · provider embeddings by default · Windows-specific storage.



