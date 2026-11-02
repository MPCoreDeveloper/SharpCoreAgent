# Tasks: One Snapshot, Served

**Feature**: `025-served-index-snapshot` · **Plan**: [plan.md](plan.md) · **Spec**: [spec.md](spec.md)

Round 16. One slice: the served call stops rebuilding what it already knew. Everything here is ordered so that the
measurement exists before the change, and the published number exists before the round is called done.

## Phase 1 — The measurement (done before the code)

- [x] **T001** Time each phase of the read path on a real index
  - Files: a temporary `tests/SharpCoreAgent.Tests/Indexing/ServedCallProbe.cs`, deleted afterwards
  - Do: open ×5, open + `build_id`, snapshot cold and warm, three calls as the lease builds them, three calls on a
    loaded instance, `find_symbol` and `index_status` warm.
  - Done: open 1 ms, snapshot cold 3 917 ms, warm call 36–43 ms — into `plan.md`. The round's premise is a
    measurement rather than an assumption, and it corrected one: the open was never the cost.

## Phase 2 — One state per database

- [x] **T002** `IndexReadCache` and the `IndexReadState` it holds
  - Files: `src/SharpCoreAgent.Indexing/Retrieval/IndexReadCache.cs`, `src/SharpCoreAgent.Indexing/FileStamp.cs`
  - Do: a bounded (four), process-wide map from database path to state, holding the reader **and** the status memo —
    the second answer the bench found (D8). `For`, `Forget`, `ForgetAll`, `EntryCount`. On a miss: drop entries whose
    database file is gone, then evict the least recently used. `FileStamp` moves out of `SharpCoreDbCodeIndex`'s
    private nest so the memo can name it.
- [x] **T003** `SharpCoreDbCodeIndex` takes both answers from the cache
  - Files: `src/SharpCoreAgent.Indexing/SharpCoreDbCodeIndex.cs`
  - Do: `_state` from the cache; `Search` and `TrySnapshot` read through `_state.Reader`; `GetStatus` reads and writes
    `_state.Status` against the file stamp; `InvalidateStatus` drops the shared memo; `Dispose` closes the handle and
    keeps both answers. `LeasedCodeIndex` and `IndexHandleCache` are not touched.
  - Done: `LeasedCodeIndexTests` passes without an edit, including the one that proves a build in another process is
    never waiting on an idle server.

## Phase 3 — The tests that make it true

- [x] **T004** The cache's own promises
  - Files: `tests/SharpCoreAgent.Tests/Indexing/IndexReadCacheTests.cs`
  - Do: two calls for one database share a state; two databases never do; `Forget` starts the next reader empty; a
    fifth database holds four and has dropped the least recently used; a database that is gone is dropped on the next
    miss. No timing assertions.
- [x] **T005** Both answers survive the instance that computed them
  - Files: the same class
  - Do: a new instance sees a build made in this process (FR-002 for the snapshot); a status computed by one instance
    is read by the next and recomputed when the file changes (FR-002 for the memo); invalidating from one drops it
    for every reader; two instances in the lease's shape share one state.
- [x] **T006** One existing test amended, and named
  - Files: `tests/SharpCoreAgent.Tests/Indexing/CodeIndexOpenTests.cs`
  - Do: `SecondOpenReusesTheSameHandle` read two statuses and expected a handle; a status no longer opens the
    database at all, so it reads through a search instead. The property it asserts — one file, one engine handle,
    however many instances read it — is unchanged.

## Phase 4 — The harness says which call it measured

- [x] **T007** `mcp bench` warms once, reports the warm-up as `cold`
  - Files: `src/SharpCoreAgent.Cli/Commands/McpBench.cs`
  - Do: `MeasureAsync` issues one untimed stdio call (the first fixture question) before the loop and returns its
    duration; `BenchReport` carries `ColdMilliseconds`; `Render` prints `· cold N ms` after `· p95 N ms`. The
    in-process half and the parity rule are unchanged.
- [x] **T008** The harness's own tests
  - Files: `tests/SharpCoreAgent.Tests/Cli/McpBenchTests.cs`
  - Do: the summary line gains `cold`; a report whose first call is slow and whose rest are fast p95s the fast ones;
    the end-to-end run over a real server still reports parity 2/2 and now a cold number too.

## Phase 5 — The number, and the record

- [x] **T009** Run `mcp bench` on this repository's index, twice, and paste both into `plan.md`
  - Files: `specs/025-served-index-snapshot/plan.md`
  - Done: p95 **261** and **248 ms**, cold **2 641** and **2 834 ms**, parity 12/12 — against 3 199 ms before the
    round. SC-001 is met, and met by the command rather than by the probe.
- [x] **T010** Restate SC-005 with the pair
  - Files: `specs/013-evidence-mcp-server/spec.md`, `specs/013-evidence-mcp-server/plan.md`
  - Do: the third measurement beside the 2026-10-29 and 2026-11-01 ones, the call the 300 ms applies to named, the
    status line updated. No other criterion of that round is touched.
- [x] **T011** Full build and suite
  - Files: `SharpCoreAgent.slnx`
  - Done: build 0 warnings / 0 errors; **1 483 passed, 0 failed, 0 skipped** (1 472 before; 11 new, one amended).
- [x] **T012** The roadmap, last and separately
  - Files: `specs/000-parity-roadmap.md`
  - Do: the Round 16 section, the rounds table row, the open-items row for round 6, and the numbering note where it
    needs one. The round lands as one commit; the roadmap entry follows it carrying the hash.

## Notes

- **The brief was corrected twice, both times by measuring.** It said "learn what the 0.36 s open leaves on the
  table", implying the open was half the cost: it is one millisecond, and the 0.36 s was a whole process. Then the
  first half (the snapshot) took the bench from 3 199 ms to 1 508 ms, not to 40 — because the *status* is 1.3 s of
  the same call. Both corrections are in `plan.md` as D1 and D8 rather than edited out of the history.
- **Not done on purpose**: no stamp-gated path that skips opening the database to decide about a *build* (D3); no
  change to `LeasedCodeIndex`, `IndexHandleCache`, the writer, the schema, the five tools or the published set; and
  nothing about the build's own 119–132 s (`002` T034 stays open).
- **The probes are deleted**, both of them. They timed phases through internals; the numbers and the method are what
  the round keeps, and `mcp bench` is the reproducible instrument that replaces them.
