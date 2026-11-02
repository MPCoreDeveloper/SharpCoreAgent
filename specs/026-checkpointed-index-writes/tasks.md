# Tasks: One Commit Per Checkpoint

**Feature**: `026-checkpointed-index-writes` · **Plan**: [plan.md](plan.md) · **Spec**: [spec.md](spec.md)

Round 17. One slice: the build stops paying the store's commit once per file. Ordered so the code exists before the
number, and the number exists before the round is called done.

## Phase 1 — The boundary

- [x] **T001** `IndexFileWriter`: the checkpoint
  - Files: `src/SharpCoreAgent.Indexing/IndexFileWriter.cs`
  - Do: `BeginCheckpoint`, `EndCheckpoint` (committed only if a batch was opened), `AbandonCheckpoint` (a no-op when
    nothing is open), `FilesInCheckpoint`, `Commits`. `RunInBatch` opens the batch lazily inside a checkpoint and joins
    the open one for every later write; outside a checkpoint it commits per call exactly as today. The counter is
    incremented where `EndBatchUpdate` is timed, so the two cannot disagree.
  - Done: no caller changes, and every existing path behaves identically (FR-006).

- [x] **T002** `IndexWriter`: four boundaries and the vanished set
  - Files: `src/SharpCoreAgent.Indexing/IndexWriter.cs`
  - Do: `CheckpointFiles = 32` and an optional constructor parameter for tests; the loop opens a checkpoint, commits
    every 32 carried files, commits before the budget break and before a cancellation leaves the writer, and abandons
    on any other exception; `RemoveVanished` removes its whole set in one checkpoint; `IndexWriteResult.Commits` comes
    out.
  - Done: a build of five files commits 3 times (one write, one edges, one meta) instead of 7.

- [x] **T003** The count reaches the developer, and a prune gets the same rule
  - Files: `src/SharpCoreAgent.Domain/Index/IndexBuildReport.cs`,
    `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs`, `src/SharpCoreAgent.Cli/Commands/IndexCommand.cs`
  - Do: `IndexBuildReport.Commits` (default `0`); `BuildAsync` and `PruneAsync` fill it; `index --verbose` prints one
    `commits` line in the block it explains. `--json` untouched.

## Phase 2 — The tests that make it true

- [x] **T004** The arithmetic, on a build small enough to count by hand
  - Files: `tests/SharpCoreAgent.Tests/Indexing/CheckpointTests.cs`
  - Do: five files with `checkpointFiles: 2` → 3 write commits plus edges and meta = 5, and the same five files with
    the default → 3. The counters (`added`, `chunks`, `symbols`) are identical in both runs, which is D8's promise that
    a checkpoint is a commit boundary and not an accounting one.

- [x] **T005** An abandoned checkpoint leaves the store as the last commit left it
  - Files: the same class
  - Do: a writer whose second file throws — the first file written inside an open checkpoint, the checkpoint abandoned,
    and the `files` table empty afterwards. This is the engine's `CancelBatchUpdate` doing what D4 claims, asserted
    rather than trusted.
  - Done: the store holds zero rows, not one.

- [x] **T006** The two paths that stop a build early keep what they already wrote
  - Files: the same class
  - Do: a zero processor-time budget with a checkpoint larger than the corpus commits once (for `meta`) and reports
    `partial`; a checkpoint that already carries a file commits it when the budget expires between files; a cancelled
    token raised before the first file leaves no batch open and no row behind.

- [x] **T007** A prune removes its whole set in one commit
  - Files: the same class
  - Do: five indexed files, three deleted from disk, `PruneAsync` → three rows gone, two remain, and the report says
    the work was one commit.

- [x] **T008** The command line says how many commits the run made
  - Files: `tests/SharpCoreAgent.Tests/Cli/IndexCommandTests.cs`
  - Do: `--verbose` prints `commits` with a number, a build without `--verbose` prints none, and `--json` still has no
    `commits` key.

## Phase 3 — The number, and the record

- [x] **T009** One `--rebuild` on this repository's index, and both runs in `plan.md`
  - Files: `specs/026-checkpointed-index-writes/plan.md`
  - Done: **2 m 53 s** and **26** commits against D21's 17 m 51 s and 700; `write` 36.9 s and the commit inside it 37.5 s
    against 15 m 47 s and 15 m 49 s; `communities` 2 m 8 s and 74 % of the run. The terminal's own output is quoted
    beside the table.

- [x] **T010** The record in `002`
  - Files: `specs/002-code-evidence-index/research.md`, `specs/002-code-evidence-index/tasks.md`
  - Do: D22 records what shipped, what it measured and what it left; T034's note names the next lever and keeps the task
    open, because SC-002's 5 000-file tree and SC-006's 61× are still unmeasured.

- [x] **T011** Full build and suite
  - Files: `SharpCoreAgent.slnx`
  - Done: build clean at zero warnings; **1 494 passed, 0 failed, 0 skipped** — eleven new, against 1 483.

- [ ] **T012** The roadmap, last and separately
  - Files: `specs/000-parity-roadmap.md`
  - Do: the Round 17 section, the rounds-table row, and the round-0 open item's numbers. Lands as its own commit carrying
    this one's hash, the shape rounds 9–16 took.

## Notes

- **The brief's own constant was revised by the measurement, and left where it was.** 32 files a checkpoint removed 96 %
  of the commits, and the commit that remains is still half of what the write phase costs — a bigger group would help.
  What stopped the round is that a checkpoint is a buffer as well as a failure unit, and how large that buffer may grow is
  a question about memory that this slice did not take. It is recorded in `plan.md` (D3), in `002` D22 and in T034's note
  rather than left for someone to rediscover.
- **One measurement was thrown away, and why.** The first `--rebuild` wrote its log into `artifacts/`, inside the
  workspace, so the walk indexed a file that was still growing and the run finished by calling its own index `stale`. The
  second run kept its log in `%TEMP%`, scanned the same 740 files and finished `fresh`; the phase numbers in `plan.md` are
  the second run's. The first run's timings were within two seconds of the second's, so the discard cost nothing but the
  wait — and it is the kind of thing a probe would have hidden and the command line could not.
- **Not done on purpose**: no `--checkpoint` flag (D3), no change to `communities`, `meta` or the store's own commit, no
  parallel extraction (D9), no `--json` key (D7), and no edits to any test but the two new files — `PhaseTimingTests`,
  `RefreshBudgetTests`, `VerifyPruneTests`, `WriterLockTests` and `IncrementalIndexTests` are untouched, which is what
  makes "nothing observable changed" a claim rather than a hope.
