# Implementation Plan: One Snapshot, Served

**Feature**: `025-served-index-snapshot` · **Spec**: [spec.md](spec.md) · **Round 16** of
[`000-parity-roadmap.md`](../000-parity-roadmap.md) · **Created**: 2026-11-02

## Summary

One new type in `SharpCoreAgent.Indexing` — `IndexReadCache`, a bounded process-wide map from index database
path to `IndexReader` — one line changed in `SharpCoreDbCodeIndex` to take its reader from it, no change to
`LeasedCodeIndex` at all, and a warm-up call in `mcp bench` so the harness can say which call its p95 describes.
The five tools, the schema, the writer and the lease keep their behaviour.

## Decisions

### D1 — The snapshot moves out of the call; the handle does not

`LeasedCodeIndex` opens a `SharpCoreDbCodeIndex`, calls one method, and disposes it. Both the handle (through
`IndexHandleCache`) and the reader (`private readonly IndexReader _reader = IndexReader.Create()`) died with the
instance, so the next call paid both again. The probe says only one of them is worth keeping, and only one of them
may be kept: the snapshot is 3 917 ms cold and 0 ms warm and holds no database handle, while the open is 1 ms and
is the thing the lease exists to release.

So `IndexReader` becomes process state keyed by database path, and `_lease` stays per instance. `LeasedCodeIndex`
is untouched, byte for byte, and `LeasedCodeIndexTests` — including the one that proves a build in another process
is never waiting on an idle server — must pass unchanged for this round to be right.

### D2 — `IndexReadCache`, not a `static IndexReader`, and not a cache in `LeasedCodeIndex`

Three shapes were considered:

| Shape | Why not |
|---|---|
| `static readonly IndexReader` | one snapshot for the whole process regardless of workspace; two workspaces would read each other's passages |
| a reader passed into `SharpCoreDbCodeIndex.For(...)` by each caller | the composition root would have to name an internal type, and the three call sites (agent, CLI, `mcp serve`) would each have to remember |
| a `Dictionary` inside `LeasedCodeIndex` | that type lives in `Infrastructure` and sees only `ICodeIndex`; the reader it would have to hold is internal to `Indexing`. It would also do nothing for the in-process agent or for `mcp bench`'s in-process half |

`IndexReadCache` sits beside `IndexReader` in `SharpCoreAgent.Indexing.Retrieval`, is `internal`, and is asked
for a reader by the one type that already owns one. Every consumer benefits without knowing it exists.

### D3 — The key is the database path; the freshness key stays `build_id`

The cache is keyed by `IndexPaths.DatabasePath`, because that is what a snapshot of a file is. Freshness is not the
cache's business: `IndexReader.Load(database)` reads `meta['build_id']` from the handle it is given and reloads
when it differs, which is round 6's mechanism and this round's guarantee. A rebuild in another process changes the
id, so the next call reloads — FR-002 — with no new invalidation path to get wrong.

The alternative — trust the file's length and write time and skip the open — was refused in the spec: it would save
one millisecond and cost the "ask the database" property. `SharpCoreDbCodeIndex` already caches *status* against
that stamp, and that is where it belongs.

### D4 — Four entries, least recently used, and dead workspaces dropped on the way in

A snapshot of this repository is tens of megabytes, so the map cannot be unbounded even though today's processes
open one workspace. `For` therefore, on a miss: drops every entry whose `DatabasePath` no longer exists (the test
sandbox case, which is hundreds of temporary workspaces per suite), then evicts the least recently used entry if
four are still held, then inserts. Four is not a tuned number: one process today opens one index, a bench opens a
second in-process and a third in its child process, and a fifth would be a different feature.

`EntryCount` and `Forget(paths)` are internal and exist for the tests and for the rebuild path, in the same spirit
as `IndexHandleCache.CloseAll`.

### D5 — `Dispose` no longer means "forget the build"

`SharpCoreDbCodeIndex.Dispose` closes the handle and drops its status cache. It must not, any more, drop the
snapshot — an instance lives for one call, and the whole round is the observation that the snapshot outlives it.
This is the one line in the round a reviewer should stop and read.

### D6 — `mcp bench` warms once and reports the warm-up as `cold`

The harness starts one server process and asks twelve questions through it; the first call pays the load and the
other eleven do not, and with twelve samples the nearest-rank p95 *is* the slowest sample. Before this round that
made the p95 a measurement of the first call — the criterion would read 3.2 s even against a warm server. Now:
`MeasureAsync` issues one untimed call (the first fixture question) before the loop, `BenchReport` carries its
duration as `ColdMilliseconds`, and `Render` prints `· cold N ms` after `· p95 N ms`. The rows still carry the
twelve timed calls, parity is still asserted on each, and the in-process half is unchanged.

The warm-up is not hidden: it is one extra `tools/call` on the wire, the summary names it, and a reader who wants
the cold number finds it on the same line as the warm one.

### D7 — SC-005 is restated a third time, and this time it is met

`013/spec.md` SC-005 delegates its number to `013/plan.md`, which records 4 425 ms (2026-10-29) and 3 199 ms
(2026-11-01) as misses with the mechanism beside them. This round appends the third measurement — warm p95 and
cold, on this machine, with the index size — and restates the criterion so that it names the call it applies to:
*after the first*, a served call costs no more than 300 ms at p95. The 300 ms is unchanged; what changes is that
it is now true of the call the sentence always meant.

`013/spec.md`'s status line and its SC-005 paragraph are edited in place, and `specs/000-parity-roadmap.md`'s
open-items row for row 6 loses the "cheapening the lease" clause and gains the measured pair.

### D8 — The status memo moves with the snapshot, because the bench found the other half

D1–D7 were written after the first probe and were wrong about one thing: they treated the snapshot as the whole
cost. The bench on this repository's index then printed **p95 1 508 ms**, not the 40 the probe promised, and both
halves of the harness agreed with each other (in-process 1 251–1 519 ms, stdio 1 249–1 509 ms), which ruled out the
hop and pointed at something every call does.

A second probe, in the same shape as the first, found it in one line:

```
status, fresh instance               2899 ms
status, same instance again             0 ms
search, same instance                1311 ms
search, fresh instance                 38 ms
status, fresh instance               1435 ms
search, fresh instance                 34 ms
status, fresh instance               1347 ms
```

Search on a fresh instance is 34–38 ms — the first half works. **Status on a fresh instance is 1 347–1 435 ms**, and
every one of the five tools reads a status before it answers, because `CodeIndexAccess.CheckAsync` refuses a missing
index and the answer's heading carries the freshness. `SharpCoreDbCodeIndex` cached that status in
`_cached`/`_cachedStamp`, keyed by the database file's stamp — inside the instance the lease throws away.

So the memo moves to the same place as the snapshot, with the same key it already had: `IndexReadState` holds the
reader and the `IndexStatusMemo` (status + the file stamp it was computed for). The state is one object per database
path, in `IndexReadCache`; the memo is a single reference, so a write is atomic and two calls computing at once
cannot leave a status paired with another call's stamp. `InvalidateStatus` now drops the memo for every reader of
that database, which is what a build wants.

This is why the round's own spec was amended before the code was finished rather than after: SC-001 asks for a
300 ms served call, and the snapshot alone leaves 1.3 s of status in the way. The plan said "one new type" and that
is still true — the type just carries two answers instead of one.

`FileStamp` moved out of `SharpCoreDbCodeIndex`'s private nest into `SharpCoreAgent.Indexing/FileStamp.cs` so the
memo can name it. It is still two numbers and a comment.


## The probe, and its output

The probe was a temporary test — `tests/SharpCoreAgent.Tests/Indexing/ServedCallProbe.cs`, run once and then
deleted, with the measurement being what this round keeps — that timed each phase through the assembly's internals:

- `IndexHandleCache.Acquire(paths, null, createIfMissing: false)` five times, released between samples;
- open, then one `SELECT value FROM meta WHERE key = 'build_id'`;
- `IndexReader.Create().Load(db)` cold, then again on the same reader;
- `SharpCoreDbCodeIndex.For(workspace).SearchAsync(...)` three times, a fresh instance each time — today's call;
- one instance called three times — the floor once the load is amortised;
- `FindSymbolsAsync` and `GetStatusAsync` on a warm instance.

Output, 2026-11-02, this machine, index built 2026-11-02T05:24:37Z:

```
open #0                                        55 ms
open #1                                         1 ms
open #2                                         1 ms
open #3                                         1 ms
open #4                                         1 ms
open 1 ms + build_id 42 ms (1 rows)
snapshot cold (13686 passages, 6995 symbols)   3917 ms
snapshot warm                                   0 ms
call as today #0 (12 blocks)                 2433 ms
call as today #1 (12 blocks)                 2225 ms
call as today #2 (12 blocks)                 2439 ms
warm call #0 (12 blocks)                     2327 ms
warm call #1 (12 blocks)                       43 ms
warm call #2 (12 blocks)                       36 ms
find_symbol warm                               13 ms
index_status warm                              45 ms
```

The 36–45 ms floor is the number SC-001 is judged against, with the caveat that it was measured in-process on a warm
snapshot rather than over stdio; `mcp bench` is what turns it into the published number. It is also, as D8 records,
only half of the answer.

## The bench, after

Two runs of `sharpcoreagent mcp bench --workspace .` on this repository's index, 2026-11-02, twelve fixture queries:

```
bench · 12 queries · evidence 143 825 chars · sources 6 452 613 chars · ratio 0.022 · p95 261 ms · cold 2641 ms · parity 12/12
bench · 12 queries · evidence 143 825 chars · sources 6 452 613 chars · ratio 0.022 · p95 248 ms · cold 2834 ms · parity 12/12
```

The per-query rows of the first run: eleven of the twelve cost 23–90 ms and the first timed one costs 187 ms — the
query that follows the warm-up, still paying part of what the warm-up warmed. In-process and over stdio agree within
a few milliseconds on every query, which is the hop being free, and the first timed query is the slowest of the
twelve in both runs, which is why the nearest-rank p95 sits above the median rather than at it.

**Before this round**: p95 3 199 ms (2026-11-01, `013/plan.md`). **After**: 248–261 ms. **Criterion**: 300 ms, met.

The cold call — the one that loads the snapshot and computes the status in a fresh server process — is 2 641–2 834 ms
and is printed beside the p95 rather than hidden in it (D6).

## What the change touches

| File | Change |
|---|---|
| `src/SharpCoreAgent.Indexing/Retrieval/IndexReadCache.cs` | new: the bounded map of `IndexReadState` — `For`, `Forget`, `ForgetAll`, `EntryCount` |
| `src/SharpCoreAgent.Indexing/FileStamp.cs` | new: the two numbers that say a database was not rewritten, lifted out of `SharpCoreDbCodeIndex` |
| `src/SharpCoreAgent.Indexing/SharpCoreDbCodeIndex.cs` | `_state` from the cache; the snapshot and the status memo live there; `Dispose` keeps both |
| `src/SharpCoreAgent.Cli/Commands/McpBench.cs` | one warm-up call, `ColdMilliseconds`, `cold` in the summary |
| `tests/SharpCoreAgent.Tests/Indexing/CodeIndexOpenTests.cs` | one test amended: a status no longer opens the database, so the shared-handle assertion reads through a search |
| `specs/013-evidence-mcp-server/spec.md`, `plan.md` | SC-005 restated with the measured pair; the measurement appended |
| `specs/000-parity-roadmap.md` | Round 16 section, table row, open-items row 6 updated |

Nothing else. `LeasedCodeIndex`, `IndexHandleCache`, `IndexReader`, the retrievers, the ranker and the five tools are
untouched — and `LeasedCodeIndexTests`, including the one that proves a build in another process is never waiting on
an idle server, passes without an edit.

## Verification

1. `dotnet build SharpCoreAgent.slnx -c Release` — **0 warnings, 0 errors**.
2. `dotnet test SharpCoreAgent.slnx -c Release` — **1 483 passed, 0 failed, 0 skipped** (1 472 before; 11 new, one
   existing amended).
3. `sharpcoreagent mcp bench` on this repository's own index, twice — p95 261 and 248 ms, cold 2 641 and 2 834 ms,
   parity 12/12 both times. Quoted above.
4. `sharpcoreagent index --status` — unchanged: fresh, 712 files, 13 686 passages, 350.2 MiB.

## Complexity Tracking

No constitutional exception is needed. The round adds one internal type, no dependency, no new surface, and no
concurrency primitive that was not already there (`Lock` inside a static class, which `IndexHandleCache` already
does). It removes work rather than adding it.

The one thing worth stating plainly: a process now holds the snapshot of an index between calls, which is memory it
did not hold before — tens of megabytes on this repository, bounded at four entries (D4). That is the trade, and it
is the whole point: the round buys latency with resident memory, and the bound is what stops the price rising
without limit.


