# Feature Specification: The Rows That Already Said It

**Feature Branch**: `032-write-only-what-moved`

**Created**: 2026-11-03

**Status**: Implemented 2026-11-03 (constitution 1.2.0), awaiting its commit. Round 23 of
`specs/000-parity-roadmap.md`. It takes the number round 22 left standing — a writing run over one changed file costs
**12.2 s**, with **11.2 s** of it inside three commits — and gives back the one commit the run did not need: the pass that
writes the community columns, which rewrote every declaration on every writing run to say what the row already said.

**Input**: User description: "`031`'s next slice: the writing run, where `commit` is 12.8 s of 14.2 s."

## Why

Round 22 ended with the arithmetic pointing at the writing run, and the writing run's own phase report says where it goes:

| phase | one changed file | what it is |
|---|---|---|
| `write` | **7.2 s** | the changed file's rows and their batch commit |
| `communities` | 2.6 s | `assign` **2.4 s**: 7 305 rows rewritten |
| `meta` | 2.0 s | counters and their commit |
| `of which commit` | **11.2 s** | the three batch commits, `commits` **3** |

The middle row is the one that cannot be defended. `CommunityAssigner` read the declarations with
`SELECT id, key FROM symbols` and then wrote `community` and `centrality` for every row it had read — 7 305
`UpdateByPrimaryKey` calls and a commit, on a run that had changed one document. A build whose partition has not moved
writes the partition the store already holds.

### The measurement, before the code

Probes on this repository's own index, taken before any edit and deleted with the round:

| probe | measured |
|---|---|
| one document changed, 73.6 MiB store | **12.2 s** wall, `commits` **3**, `write` 7.2 s, `assign` 2.4 s, `meta` 2.0 s, `commit` 11.2 s |
| the same change reverted | 11.9 s, 3 commits, `write` 7.0 s, `assign` 2.1 s, `meta` 2.3 s, `commit` 10.9 s |
| one source file changed, first a 103-line one and then a 700-line one | 12.2 s and 14.5 s, `write` **7.2 s** and **7.6 s** |
| the store after those four runs | **73.6 MiB → 110.4 MiB**, in four runs of one changed file |

Two of those rows decide what this round can and cannot do.

**The write commit is not payload and not file size.** A 103-line document and a 700-line source file cost 7.2 s and
7.6 s of `write`, and the same commit at 110.4 MiB cost 7.6 s. What it follows is the tables the batch touches: a
`passages` row drags the full-table index maintenance with it, whether the batch carries one row or a thousand. That is
the store's, not this round's, and it is recorded rather than acted on.

**A commit costs seconds whatever it carries.** The `meta` batch is ten rows and its commit is 1.9–2.1 s; the community
batch is 7 305 rows and its commit is about the same. So a writing run's floor is roughly *the number of commits it
makes*, and the cheapest one to remove is the one whose rows were already correct.

### What this round does not fix

The remaining **8.3 s** of a one-changed-file run, which is now `write` **6.7 s** of commit plus `meta` **2.1 s** of
## What it does

Three changes, one rule: the pass writes a row when the answer it holds is not the answer it computed.

1. **The pass reads the answer it is about to overwrite.** `SelectSymbols` became
   `SELECT id, key, community, centrality FROM symbols`, and `SymbolNode` carries the stored label and degree beside the
   id and the ordering key, so the write has something to compare.
2. **`Write` builds the batch from the rows that differ.** A row joins the batch when its label differs ordinally or its
   degree differs as a number. When no row differs the batch is never opened: `Commits` is **0** and `assign` costs
   what the comparison costs.
3. **The comparison is made in the form a reader sees.** An unclustered declaration has no label — the pass leaves the
   array entry `null` and the store keeps "no value", which reads back as empty text; and a degree of `1` comes back as
   an `Int32`, an isolated row's as `DBNull`. Both are compared in the form the read produced, because that is the form
   the next build will see too. Getting this wrong once meant every isolated row differed and the pass rewrote them all,
   which the first version of the sandbox test caught.

Nothing else changes: no schema, no column, no flag, no exit code, no `--json` field, and no new public type.
`CommunityAssigner` and `IndexSql` are internal, and the only public number in sight — `IndexBuildReport.Commits` —
counts one commit fewer on a writing run because one fewer commit happens.

## Success criteria

- **SC-001** A writing run over one changed file at 73.8 MiB costs **9.2 s** and **2 commits**, against 12.2 s and 3
  commits before, with `assign` **0.0 s** against 2.4 s.
- **SC-002** A pass whose partition has not moved writes nothing and reports `Commits` 0 — measured by running the pass
  twice against a fresh index (`first=0 second=0` once the build has stored the answer) and pinned by
  `APassThatFindsTheSameAnswersWritesNothing`, where a document change costs two commits (the file and `meta`) and the
  stored columns are unchanged.
- **SC-003** A row whose label no community owns is taken back even when the graph has not moved:
  `AStaleLabelIsTakenBackEvenWhenTheGraphHasNotMoved` stores `c99` on an isolated declaration, rebuilds through a
  document change, and finds the empty label back for three commits (the document, the one community row, `meta`).
- **SC-004** A writing run whose graph *has* moved still writes and still pays: four files changed (source edits that
  added two declarations) ran `communities` **2.3 s** with `assign` **2.1 s** and `commits` 4.
- **SC-005** A refresh that changes nothing is unchanged by this round: **0.5–0.7 s** wall, `commit` **0**.
- **SC-006** `dotnet build -c Release` is clean at 0 warnings and the suite passes with the two new cases counted against
  1 518 — **1 520 green**.
- **SC-007** Nothing public moves: no schema, no column, no flag, no `--json` field, no `PublicAPI.Unshipped.txt` entry.

## Out of scope

- The write commit's 6.7 s and `meta`'s 2.1 s, which are whole-table index maintenance inside a batch that has a row to
  write — recorded above as the store's, with the two probes that show it is neither payload nor file size.
- Coalescing the run's two remaining commits into one. It is the next lever and it is not free: `026` bought the
  per-checkpoint failure unit on purpose, and that argument has to be answered before the boundary moves.
- The store growing 33 % across four writing runs of one changed file (round 21's garbage); `index --compact` still
  reclaims it, and nothing here compacts automatically.
- `index --prune` that removes a file (10.1 s), SC-002's 5 000-file tree, SC-006's size ratio and SC-003's p95. `002`
  T034 owns them.
- Any new persisted state, and any growth of `HeuristicSyntaxFactsExtractor`.

commit: both are whole-table index maintenance inside a batch that has a row to write, and neither moves by writing less.
The store also grew 33 % across four writing runs of one changed file, which is round 21's garbage and the reason every
number here was taken after an `index --compact`. Both stay on `002` T034 with the rest of the floor.
