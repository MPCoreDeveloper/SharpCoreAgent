# Feature Specification: The Pass That Changed Nothing

**Feature Branch**: `031-cheap-no-op-refresh`

**Created**: 2026-11-03

**Status**: Implemented 2026-11-03 (constitution 1.2.0), awaiting its commit. Round 22 of
`specs/000-parity-roadmap.md`. It answers the number round 21 measured and left standing — a turn-start refresh on a
workspace nobody touched costs **5.5 s** to establish that it has nothing to do — and it answers it by writing nothing
rather than by reading less.

**Input**: User description: "`030`'s next slice: a refresh that changes nothing costs 5.5 s a turn, and compacting the
file could not move it."

## Why

Round 21's evidence paragraph ends with the sentence this round starts from:

> a run that changes nothing still costs 5.6 s with `meta` 2.0 s and 3.4 s of open — unchanged, because the commit is
> not size-bound

Two numbers, and both were scans of a table the pass was not changing: `SELECT MAX(id)` over six tables when the writer
opened (≈**3.6 s**, of which `chunks` alone is **2 598 ms** of 14 434 rows), and four `COUNT(*)` in `WriteMeta`
(`chunks` **2 700 ms**, the whole of what the four counts cost). The batch that recorded them cost the rest.

### The measurement, before the code

Temporary probes on this repository's own index, deleted with the round (`specs/002-code-evidence-index/research.md`
D27 keeps them beside the code that answers them):

| probe | measured |
|---|---|
| the six `MAX(id)` reads at writer open | ≈3.6 s |
| four `COUNT(*)` in `WriteMeta` | 2 700 ms |
| ids read lazily, counters still counted | `meta` **5 109 ms**, of which the commit **5 107 ms**; run 5.4–5.7 s |
| the same, the four counts forced back on | `meta` **5 235 ms**, of which the commit **2 537 ms**; run 5.4–5.7 s |
| the `meta` batch alone, before either change | **1 774 ms** on the 73.2 MiB file round 21 left |

Three of those rows are the finding.

**The scans did not leave the run, they moved into its commit.** Reading a table's `MAX(id)` when the writer opened was
3.6 s outside every phase; reading it lazily removed that and made the `meta` phase 5 109 ms, of which the commit was
5 107 ms. SharpCoreDB reads the pages a batch touches, so the 2.7 s of counting `chunks` was also the read that made the
following commit cheap. Take the count away and the commit pays for the pages instead.

**So the fix is not to read less, it is to write nothing.** A pass that wrote no row and removed no file has, by
definition, left every table as the last completed build left it: the counters it would record are the counters already
stored, and the lowest id it needs is the one already on disk. Nothing it could say would tell a reader anything the
store does not already say.

### What this round does not fix

The **writing** run: one changed file on this index costs **14.2 s**, of which **12.8 s** is commit time spread over the
row, edge, community and `meta` batches, and `index --prune` costs **10.1 s** when it removes one file. Those, SC-002's
5 000-file tree and SC-006's size ratio stay on `002` T034, which this round shortens rather than closes.

## What it does

Four changes, one rule: a pass that changed nothing writes nothing, and therefore reads nothing.

1. **A table's ids are read when the build first writes to that table.** `IndexIdSequence.For` no longer reads
   `MAX(id)` for six tables when the writer opens; `Next(table)` reads it once, on the first id that table hands out.
   `meta` is not one of those tables — it is keyed by its own column.
2. **`WriteMeta` records nothing when nothing changed.** A no-op pass borrows the counters the last completed build
   stored instead of counting four tables, and if every key but `build_id` and `build_utc` already holds the value it
   would write, the batch is not opened at all.
3. **A prune recounts only when it removed a row.** A prune that dropped nothing has the same nothing to record.
4. **A build's own status stops counting orphan rows.** It has just written those rows from the files it scanned, so it
   cannot have left one behind for a file it did not insert — and the count is 2.6 s of the 3.1 s `index --status` takes
   on this index, `SELECT file_id FROM chunks` over 14 434 rows.

Nothing else changes: no schema, no column, no flag, no exit code, no `--json` field, and no new public type.
`--status` asked by a reader and `index --verify` still count orphans; only the status a build or a compaction reports
about its own rows leaves it at 0, and `IndexStatus.OrphanRows` now documents that 0 means "not counted here" rather
than "checked and clean".

## Success criteria

- **SC-001** A refresh on an unchanged workspace costs under 1 s and commits no batch. Measured on this repository's
  771-file index: **507–662 ms** of a 645–769 ms wall, `commit` **0**, `meta` 1–2 ms, against 5.5 s before.
- **SC-002** A pass that changed nothing leaves `build_id`, `build_utc`, `file_count`, `chunk_count`, `symbol_count` and
  `edge_count` exactly as the last writing build wrote them, and `Commits` is 0 — `CheckpointTests`'
  `APassThatChangesNothingCommitsNothing`: 5 added, 0 removed, 5 unchanged, 0 commits.
- **SC-003** A pass that removed a file still records: `file_count` becomes 4, `chunk_count` differs from the earlier
  build's and `build_id` is new — `APassThatRemovedAFileRecountsAndRecords`.
- **SC-004** An incomplete pass still records: `build_state` moving off the seed value is a change worth a commit, so a
  zero-budget run keeps its one `meta` batch — `AZeroBudgetCommitsNothingButMeta`.
- **SC-005** `--status` after any number of no-op passes still reports `state fresh`, the timestamp of the last build
  that wrote, and the counters it wrote: `fresh · built 2026-11-03T18:30:43Z · 771 indexed · 0 stale · 0 orphan rows`.
- **SC-006** A prune that removes nothing costs under 1 s and writes nothing: **465–554 ms** measured, against 10.1 s
  for the same command removing one file.
- **SC-007** `dotnet build` in Release is clean at 0 warnings and the suite passes with the two new cases counted
  against 1 516 — 1 518 green.

## Out of scope

- The writing run's floor: `commit` is 12.8 s of the 14.2 s one changed file costs, and this round does not touch it.
- A prune that removes a file (10.1 s), the cold build (1.07 s per file), SC-002's 5 000-file tree, SC-006's ratio
  (73.6 MiB against 5.98 MB of tracked source) and SC-003's latency, which has never been measured. `002` T034 owns them.
- Repairing an interrupted build. A build that died between its rows and its `meta` write leaves counters describing the
  build before it; the next pass that writes anything recounts all four tables, and `--rebuild` is the repair for an
  index that never writes again. `002` `research.md` D27 records it rather than fixing it.
- Any new persisted state (a "last checked" stamp, a workspace digest, a compaction-style trigger) and any growth of
  `HeuristicSyntaxFactsExtractor` — the refresh's own cost is what this round moved.

