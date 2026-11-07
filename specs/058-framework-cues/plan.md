# Plan — framework cues

## Approach: a query-time scan, not a build-time fact table

Two shapes were possible. A `facts` table filled at build time is the durable one, but it needs a new
table, writer changes, a reader/snapshot change and an `analyzer_contract` bump that rebuilds every
user's index — a schema round. A query-time scan over the passage text the index already keeps needs
none of that: it is one new read-port method plus a rule table, and it cannot corrupt or stale a build.

The trade-off is cost per call: the scan reads the text of every passage once. That cost is bounded by
the same passage set the vector leg already walks, runs only when the model asks, and returns a capped
list. Build-time storage is recorded below as a future optimisation, not a gap.

## Layers

| layer | change |
|---|---|
| Domain | `CodeCueRef`, `CueKinds` (closed list), `ICodeIndex.FindCuesAsync` |
| Indexing | `CueRetriever` (rule table over `ChunkRow.Text`), `SharpCoreDbCodeIndex.FindCuesAsync`, `SharpCoreAgentIndex` delegation |
| Infrastructure | `FindCuesTool`, `LeasedCodeIndex` delegation, DI registration |
| Tests | `CueRetrieverTests`, `RetrievalToolTests`, `CodeIndexSearchTests`, `LeasedCodeIndexTests`, `TaskToolRegistrationTests` |

## Decisions

- **No `HeuristicSyntaxFactsExtractor` change.** Cues are computed at query time in a separate class,
  so the C#/VB rule is untouched and the Roslyn oracle remains the C# path. This is the whole reason
  the slice is a scan rather than an extraction pass.
- **Candidates, labelled.** Every result is a regex hit over source; the tool and the docs call it a
  candidate, exactly as Airk labels `syntax_candidate`.
- **Closed kind list in Domain.** `CueKinds.Supported` is the one list the tool validates against, so
  the schema, the error message and the constant cannot drift.
- **Line arithmetic off the passage.** A passage stores its first line, so a cue's line is
  `passage.StartLine + indexWithinPassage`; the citation is the same `path:line` a search would print.

## Complexity Tracking

The per-call full-text scan is the only added cost. It is justified by avoiding a schema migration for
a first version. If a latency benchmark shows the scan hurts, the recorded follow-up is a build-time
`facts` table with an `analyzer_contract` bump. No constitutional gate is relaxed; the tool is one more
read-only `ITool`.

## Constitution check

- Domain stays BCL-only (`CodeCueRef`, `CueKinds` are plain records/constants).
- New capability is an `ITool`; no `Process.Start`, no network, no new dependency.
- No `HeuristicSyntaxFactsExtractor` growth for C#/VB.
- SharpCoreDb's database and vector/SIMD graph are untouched; the scan reads loaded passage text.
