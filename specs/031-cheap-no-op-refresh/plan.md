# Implementation Plan: The Pass That Changed Nothing

**Feature**: `031-cheap-no-op-refresh` · **Spec**: [spec.md](spec.md) · **Tasks**: [tasks.md](tasks.md)

Round 22. One slice: a refresh that has nothing to do stops paying 5.5 s to say so. Four changes, none of them to the
loop, the schema or any surface — the writer simply stops writing when no row changed.

## Summary

`IndexIdSequence.For` stops reading six `MAX(id)` values at construction: `Next(table)` reads the table's `MAX(id)` on the
first id that table hands out, which for a no-op pass is never. `IndexWriter.WriteMeta` borrows the counters
`SharpCoreDbIndexMaintainer` already loaded and asks `Describes` whether every key but `build_id` and `build_utc` already
holds the value it would write; when it does, `_files.WriteMeta` is not called and no batch, no commit and no counter
appears. `SharpCoreDbIndexMaintainer.PruneAsync` calls `RefreshCounters` only when it removed a row.
`SharpCoreDbCodeIndex.GetStatus(countOrphans:)` lets the status a build or a compaction reports about its own rows skip
the orphan walk, while a reader's `GetStatus()` and `--verify` still count.

Nothing else changes: no schema, no column, no flag, no exit code, no `--json` field, no new public type, and no
behaviour on a pass that did change something.

## Decisions

### D1 — The cost was the commit, not the scans

Lazy ids removed ≈3.6 s from the open and the run did not get faster: `meta` became **5 109 ms** with **5 107 ms** of it
inside the commit. Forcing the four counts back on split the same work 5 235 ms / **2 537 ms**. So the store was reading
the pages the `meta` batch touched during the `COUNT(*)` calls, and the commit paid for whatever the counts had not
prefetched. A round that only made the reads lazy would have moved the cost, which is why the fix is the write.

The sequence matters: the first measurement said "two scans cost 6 s", the second said "one of them is a prefetch", and
only the third said "then do not write". Two of those three readings were wrong.

### D2 — Ids are read per table, on first use, and `meta` is exempt

`IndexIdSequence` keeps one `long?` per table; `Next` fills it from `IndexSql.MaxId` the first time the build asks. A
build that inserts a file before it inserts a passage is unchanged (each table's read happens before that table's first
write, under the same writer lock), and a build that writes to no table reads nothing. `meta` is keyed by its own column
and never asked for an id, so it is not in the map.

### D3 — "Nothing changed" is decided by comparing values, not by trusting a counter

`Describes(rows, stored)` compares key by key, exempting `build_id` and `build_utc`, because those two name the run
rather than describe the workspace: a pass that records them has written a row that says only what time it is. Every
other key — `build_state`, the four counters, the schema, parser and analyzer contract keys — is compared. A build that
added, changed or removed a row writes anyway, because its counters or its state differ; so does one whose schema key
moved after an upgrade.

The alternative — `if (report.Added + Changed + Removed > 0) WriteMeta` — would have been a second copy of the same
condition, computed from a report this type does not own, and it cannot see `build_state`.

### D4 — A build's own status does not count orphans; a reader's does

The count is `SELECT file_id FROM chunks` over 14 434 rows: 2.6 s of the 3.1 s `index --status` takes. A build has just
written its rows from the files it scanned, so a row for a file it did not insert is not something it can have produced —
its own report does not need the walk. `GetStatus()` (the reader's) keeps it, `--verify` keeps it, and
`IndexStatus.OrphanRows` documents 0 as "not counted here".

The memo is keyed by the file's stamp alone, so only an answer that counted the rows may be stored in it: `GetStatus`
memoises `countOrphans && stamp is not null` and leaves the previous entry alone otherwise. A memo written by a build's
uncounted status would have served `0` to the next reader for the life of the file.

### D5 — A prune recounts only when it removed a row

`PruneAsync` already knows how many rows it deleted. With none deleted its counters are the ones it just read, so the
recount is `RefreshCounters` behind `removed > 0`. Measured: a prune that removes nothing is 465–554 ms; one that removes
a file is 10.1 s, and that figure is unchanged, because a writing prune must count.

### D6 — What a no-op pass does not record, and why that is honest

`build_id` and `build_utc` keep naming the last build that changed something, so an index refreshed twelve times still
reports the timestamp of its last write. That is what `--status` should say — the age of the data, not of the check — and
`IndexReader`/`--status` were verified against it after four consecutive no-op passes.

The gap this leaves is real and is recorded, not hidden: a build that died between its rows and its `meta` write leaves
counters describing the build before it, and a later no-op pass now leaves them wrong instead of quietly correcting them.
The next pass that writes anything recounts all four tables (`stored` is only borrowed when no table changed), and
`--rebuild` repairs an index that never writes again.

## Files

| file | change |
|---|---|
| `src/SharpCoreAgent.Indexing/IndexIdSequence.cs` | lazy per-table `MAX(id)`, `Next` filling it |
| `src/SharpCoreAgent.Indexing/IndexFileWriter.cs` | the call site that reads ids through the sequence |
| `src/SharpCoreAgent.Indexing/IndexWriter.cs` | `WriteMeta` borrows `stored`, `Counter`, `MetaKey`, `Describes`, the early return |
| `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs` | `RefreshCounters` behind `removed > 0`; `GetStatus(countOrphans: false)` at the build, prune and compaction sites |
| `src/SharpCoreAgent.Indexing/SharpCoreDbCodeIndex.cs` | `GetStatus(bool countOrphans)`, the memo rule, `CountState`'s parameter |
| `src/SharpCoreAgent.Domain/Index/IndexStatus.cs` | `OrphanRows` documents 0 as "not counted here" |
| `tests/.../Indexing/CheckpointTests.cs` | `APassThatChangesNothingCommitsNothing`, `APassThatRemovedAFileRecountsAndRecords`, a `Meta(sandbox)` helper |
| `specs/002-code-evidence-index/research.md` | D27 with the three probe rounds and the before/after |
| `specs/002-code-evidence-index/contracts/index-schema.md`, `data-model.md` | what `meta` records, and when it records nothing |

## Complexity Tracking

Nothing is added: no type, no column, no persisted key, no configuration, no dependency. `Describes`, `Counter` and
`MetaKey` are three private methods on the type that owns the batch, `IndexIdSequence` keeps one nullable field per table
where it kept a dictionary, and `GetStatus(bool)` is a parameter on a method that already existed with one caller.

The comparison is the one thing worth naming as a cost: "is this batch worth writing" is a policy, and it lives in the
writer rather than at the call site because the call site cannot see `build_state`. Its correctness is pinned by three
tests — a pass that changes nothing commits nothing, a pass that removed a file recounts and records, and a zero-budget
pass still records its state — and by the existing cases that write and then read the counters back.

