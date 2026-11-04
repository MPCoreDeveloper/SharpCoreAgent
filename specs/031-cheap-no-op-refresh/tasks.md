# Tasks: The Pass That Changed Nothing

**Feature**: `031-cheap-no-op-refresh` · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Round 22. Nine tasks, one slice. T001 is the probe sequence that found the real cost — three rounds, two of them wrong;
T002–T005 are the change; T006–T007 are the evidence; T008–T009 are the gates and the record.

- [x] **T001** Temporary probes on this repository's index, before any code: `MAX(id)` over six tables ≈3.6 s at the
  writer's open; four `COUNT(*)` in `WriteMeta` 2 700 ms; ids read lazily with the counts still paid — `meta` 5 109 ms,
  commit 5 107 ms; the four counts forced back on — 5 235 ms and 2 537 ms; the `meta` batch alone 1 774 ms (D1, spec's
  table).
- [x] **T002** `IndexIdSequence.For` keeps one nullable id per table and `Next` reads `IndexSql.MaxId` on the table's
  first id; `IndexFileWriter`'s call site unchanged; `meta` never asks (D2, FR-001).
- [x] **T003** `IndexWriter.WriteMeta` takes the counters `stored`, compares with `Describes` (every key but `build_id`
  and `build_utc`), and returns without calling `_files.WriteMeta` when the batch would say nothing new; `Counter` and
  `MetaKey` beside it (D3, FR-002).
- [x] **T004** `SharpCoreDbIndexMaintainer.PruneAsync` recounts only when it removed a row (D5, FR-003).
- [x] **T005** `SharpCoreDbCodeIndex.GetStatus(bool countOrphans)`: the build, prune and compaction sites pass `false`,
  the reader's `GetStatus()` and `VerifyAsync` keep the walk, the memo stores only counted answers, `IndexStatus.OrphanRows`
  documents 0 as "not counted here" (D4, FR-004).
- [x] **T006** `CheckpointTests`: `APassThatChangesNothingCommitsNothing` (build, then a pass over the same workspace —
  5 added, 0 removed, 5 unchanged, `Commits` 0, every counter and `build_id` unchanged) and
  `APassThatRemovedAFileRecountsAndRecords` (one file deleted — `file_count` 4, a new `build_id`, a `chunk_count` that
  differs), with a `Meta(sandbox)` helper that reads the stored keys (SC-002, SC-003).
- [x] **T007** Measure it: four consecutive `index` runs on this repository, 645–769 ms wall / 507–662 ms inside, `commit`
  **0**, `meta` 1–2 ms, against 5.5 s; two `--prune` runs that remove nothing at 465–554 ms; `--status` afterwards still
  `fresh` with the last writing build's timestamp and the counters it wrote (SC-001, SC-005, SC-006).
- [x] **T008** `dotnet build -c Release` at 0 warnings and `dotnet test` green: 1 518 of 1 518, with `CheckpointTests`
  alone at 11, and `AZeroBudgetCommitsNothingButMeta` still green (SC-004, SC-007).
- [x] **T009** Record it: `specs/002-code-evidence-index/research.md` D27 with the three probe rounds, the measured
  before/after, and the interrupted-build gap; `contracts/index-schema.md` and `data-model.md` for when `meta` is not
  written; round 22 in `specs/000-parity-roadmap.md` (the rounds table, the round section) and the note on `002` T034.

## Not in this round

- The writing run: `commit` is 12.8 s of the 14.2 s one changed file costs, and this round does not touch a batch that
  changes a row.
- A prune that removes a file (10.1 s), the cold build (1.07 s per file), SC-002's 5 000-file tree and SC-006's ratio.
  `002` T034 stays open for those.
- Repairing an interrupted build (D6): recorded in D27, repaired by the next writing pass or by `--rebuild`.
- Any new persisted state — a "last checked" stamp, a workspace digest, a trigger — and any growth of
  `HeuristicSyntaxFactsExtractor`.
