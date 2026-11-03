# Implementation Plan: The File the Index Left Behind

**Feature**: `030-index-compaction` · **Spec**: [spec.md](spec.md) · **Tasks**: [tasks.md](tasks.md)

Round 21. One slice: the index file stops being 10.8× the data it holds — `index --compact` gives the dead pages back,
and a finished rebuild does it without being asked, because a rebuild is what leaves the most behind.

## Summary

`SharpCoreDbIndexMaintainer` gains one method, `CompactAsync`, which asks the store for a full vacuum under the same
writer lock every other write takes and returns an `IndexCompaction`: the file before, the file after, the bytes
reclaimed, the elapsed time, whether it ran, and one note. `RebuildAsync` calls the same core after a complete build and
folds the outcome into the report it already returns as one note and the compacted status.

The CLI gains `--compact` beside `--status`, `--verify`, `--prune` and `--rebuild`, with a prose block and a JSON
document; `--prune --compact` is allowed, `--status`/`--verify` beside it is refused.

Nothing else changes: no schema, no column, no persisted state, no new commit, and no work on the turn-start refresh.

## Decisions

### D1 — The rewrite is the store's `VacuumMode.Full`, and the other two modes are not

Measured on a copy of this repository's index: `Full` reclaims 714.6 MiB of 787.9 MiB in 0.6 s, `Quick` and
`Incremental` reclaim 0.0 MiB in 0.0 s. The package documents the three modes as one idea — "reclaim space and reduce
fragmentation" — and for a single-file database only one of them does it. A round that chose `Incremental` because it
sounds cheaper would ship a no-op with a report saying so.

The rewrite also survives the handle cache: the probe ran it with `IndexHandleCache`'s handle open in the same process
and got the same 0.7 s and the same numbers, so the compaction does not need `CloseAll` and does not disturb the reader
the tools share.

### D2 — A rebuild compacts, a writing run does not

The cost is proportional to the live data, not to the dead space: 0.6 s for 73.2 MiB here, 0.02 s for a one-file index.
That is cheap for a command a developer typed and it is not cheap for a turn — the refresh budget is 3 s and round 20
measured a writing run at 11.3 s before this round touched it. So:

- `RebuildAsync` compacts when it finished the workspace, and leaves the file alone when it did not. An incomplete
  rebuild has spent its budget and will grow the file again on the next run, so compacting there would be paying twice.
- `EnsureFreshAsync` never compacts. A refresh that indexed one changed file already pays 2.2 s of `communities` and
  7.0 s of commits; adding a whole-file rewrite to that is the wrong trade and this round does not make it.
- `--compact` is the door for everything else: an index that drifted to 10.8× before this round shipped needs one
  command, not a rebuild.

### D3 — There is no ratio to trigger on, so the trigger is the operation

The obvious design — "compact when the file is more than twice its live data" — has no honest input. `StorageStatistics`
reports `used` 86 % / `free` 14 % for the bloated file and 85 % / 15 % for the compacted one, with fragmentation 0.0 in
both states: those numbers describe the file the store is managing, not the distance between the file and its rows. The
only way to learn what a compaction would leave is to perform one.

So the policy is stated instead of inferred: a rebuild compacts; anything else is a command. That is also the honest
reading of how the file got here — the plateau is produced by writes, and a rebuild is the write that produces the most.

### D4 — Outcomes are reported, not thrown, and a missing index is not an error

`CompactAsync` holds the writer lock for at most `LockWaitBudget` and reports a lost lock the way `PruneAsync` does
(`Succeeded = false`, `BuildingNote`). The store's own refusal is reported the same way, with its message in `Note`, and
the sizes fall back to the file on disk so a refused rewrite does not print `0 → 0 bytes`.

A workspace nobody indexed has nothing to compact: the call succeeds with `there is no index to compact` and creates no
database. The writer lock is a file in the index directory, so the directory exists afterwards — the same as `--prune` —
and the test asserts the database file, which is the thing that matters.

### D5 — The report prints the row counts, because the point is that they did not change

A compaction that gave back 714.6 MiB and lost 14 368 passages would be a worse index, not a smaller one. The prose
block therefore prints `content` after the rewrite, and the tests assert both halves: the file got smaller and every
row, hash and edge is still there (`--verify`: 766 files, 0 mismatches, 0 orphans).

The rebuild's note is one line in the existing `notes` list — `compacted 714.6 MiB in 0.6 s (787.9 MiB -> 73.2 MiB)` —
and the report's elapsed time is re-read after it, so the `wrote … in 49.2 s` line covers the whole command while the
phase block still accounts for the build alone. That is the convention the report already uses for the lock wait and
the status read.

## Files

| file | change |
|---|---|
| `src/SharpCoreAgent.Domain/Index/IndexCompaction.cs` | new: the result record, `ReclaimedBytes` derived |
| `src/SharpCoreAgent.Domain/Index/ICodeIndexMaintainer.cs` | `CompactAsync` |
| `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs` | `CompactAsync`, `CompactCoreAsync`, `CompactAfterBuildAsync`, the rebuild hook, three small renderers |
| `src/SharpCoreAgent.Indexing/SharpCoreAgentIndex.cs` | delegate |
| `src/SharpCoreAgent.Cli/CliOptions.cs` | `IndexCompact`, `--compact`, two refusals |
| `src/SharpCoreAgent.Cli/Commands/IndexCommand.cs` | the action, the prose block, the JSON document |
| `src/SharpCoreAgent.Cli/HelpText.cs`, `README.md` | one row each |
| `src/SharpCoreAgent.Domain/PublicAPI.Unshipped.txt` | the new public surface |
| `tests/.../Indexing/CompactionTests.cs` | four cases |
| `tests/.../Cli/IndexCommandTests.cs`, `IndexCommandParsingTests.cs` | three cases |
| `tests/.../Infrastructure/IndexRefreshGateTests.cs` | the fake maintainer answers the new member |
