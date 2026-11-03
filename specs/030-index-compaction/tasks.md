# Tasks: The File the Index Left Behind

**Feature**: `030-index-compaction` · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Round 21. Ten tasks, one slice. T001 is the probe that answered round 20's open number; T002–T006 are the change;
T007–T008 are the evidence; T009–T010 are the gates and the record.

- [x] **T001** Four temporary probes on a copy of this repository's index and on a synthetic one, before any code: the
  file is 787.9 MiB and its rows are 24 MB of characters; `VacuumAsync(Full)` gives back 714.6 MiB in 0.6 s with every
  row intact; `Quick` and `Incremental` give back 0.0 MiB; `StorageStatistics` reports the same 86/14 split before and
  after and fragmentation 0.0 in both; a one-file index grows 6.1 → 14.1 MiB, `DELETE` reclaims nothing, `Full` returns
  6.1 MiB in 0.02 s; 2 000 single-row updates cost 57.3 s (28.6 ms each) and change no size (D1, D3, spec's table).
- [x] **T002** `Domain/Index/IndexCompaction.cs`: the result record — before, after, `ReclaimedBytes` derived, elapsed,
  `Succeeded`, one `Note` — and `ICodeIndexMaintainer.CompactAsync` with the reason it exists (FR-001).
- [x] **T003** `SharpCoreDbIndexMaintainer.CompactAsync` under the writer lock, `CompactCoreAsync` as the lock-free core,
  and the missing-index answer that creates nothing (FR-002, FR-005, D4).
- [x] **T004** `RebuildAsync` compacts a rebuild that finished the workspace and leaves an incomplete one alone; the
  outcome rides in `Notes` and the report's status and elapsed time are re-read after it (FR-003, D2, D5).
- [x] **T005** `SharpCoreAgentIndex.CompactAsync` delegates; the CLI gains `IndexCompact`, `--compact`, the two refusals
  (`--rebuild` refuses it, `--compact` refuses `--status`/`--verify`, `--prune --compact` allowed), the prose block and
  the JSON document; `HelpText` and `README.md` carry one row each (FR-004, FR-006).
- [x] **T006** `PublicAPI.Unshipped.txt` names the new surface, and `IndexRefreshGateTests.FakeMaintainer` answers the new
  member instead of failing to compile (NFR-001).
- [x] **T007** `CompactionTests`: dead space is given back and the rows survive; a second compaction reclaims nothing; a
  workspace nobody indexed creates no database and says so; a rebuild compacts what it left behind and says so — and says
  it first, because a corpus with skipped binaries produces more notes than a report prints (SC-001, SC-002, SC-003,
  SC-005).
- [x] **T008** `IndexCommandTests` and `IndexCommandParsingTests`: the block prints before, after, content and state; the
  JSON carries the same bytes as the file on disk; a workspace without an index prints `nothing to reclaim`; the two
  refusals and the one allowed combination (SC-002, SC-005).
- [x] **T009** `dotnet build` and `dotnet test` on the solution in Release: 0 warnings and the suite green with the new
  cases counted against 1 508; then `index --rebuild` and `index --verify` on this repository, with the file size before
  and after and the verification's 766/0/0 (SC-001, SC-003, SC-006).
- [x] **T010** Record the decision in `specs/002-code-evidence-index/research.md` as D26 with the probes beside it, and
  round 21 in `specs/000-parity-roadmap.md`: the round section, the rounds table, and `002` T034's open item — the
  787.9 MiB answered, the writing-run floor still open.

## Not in this round

- Compaction on a writing run or on the turn-start refresh (D2), and any threshold or schedule (D3).
- The store's record format, page size, update path and the 28.6 ms single-row update: D26 records the measurement and
  nothing acts on it here.
- What round 20 left standing — the `commit` line, the open and no-op overhead, SC-002's 5 000-file tree, SC-006's 61×.
  `002` T034 stays open for those.
