# Tasks: The Clusterer's Own Steps

**Feature**: `027-community-pass-steps` · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Round 18. Every task is verifiable by a test, a command or a number in the plan.

## Phase 1 — The names

- [x] **T001** Add `nodes`, `adjacency`, `louvain`, `degree` and `assign` to `IndexPhases`, ordered after
  `communities` and before `meta`, and turn `IsNested` into a lookup over the six nested names (D1, D2).
- [x] **T002** Document the split where a reader meets it: `IndexPhases`'s class remark, and `IndexPhase.Name` and
  `IndexPhase.Nested` in the Domain (NFR-001 — documentation only, no member changes).

## Phase 2 — The clock

- [x] **T003** Add the `Func<Task<T>>` overload to `IndexPhaseClock` and make the `Func<ValueTask<T>>` one return its
  value instead of dropping it (D3). Both are needed because the store's algorithms return `Task<T>` and the build's own
  awaited steps return `ValueTask<T>`.
- [x] **T004** `IndexPhaseClockTests`: the nested set is exactly the clusterer's five plus `commit`; the five follow
  `communities` and stop before `meta`; a step that produces a value is timed across its await and hands the value back, on
  both overloads.

## Phase 3 — The pass

- [x] **T005** `CommunityAssigner.AssignAsync` takes the clock and records `nodes` (twice: the read, then the position map)
  and `adjacency` (twice: the read, then the renumbering) (D3).
- [x] **T006** Split `PartitionAsync`: `ReadGraph` and `BuildGraph` for the graph, `RankAsync` for Louvain and the
  numbering, one `MeasureAsync` for the metric, and the two fill loops left in the parent (D5). Behaviour identical.
- [x] **T007** `Write` charges `BeginBatchUpdate` and `EndBatchUpdate` to `commit` and returns the one commit it made;
  `CommunityAssignment` carries `Commits` and `IndexWriter` adds it to the report (D4).
- [x] **T008** `PhaseTimingTests`: the commit test sums the `commit` step rather than every nested one, and a new test pins
  `nodes`/`adjacency`/`assign` as nonzero with `louvain`/`degree` at exactly zero on a corpus with no relation.
- [x] **T009** `CommunityAssignerTests`: a two-cluster corpus reports all five steps as nonzero, nested, each no larger
  than `communities`.
- [x] **T010** `CheckpointTests`: the three expected counts move by one, because the clusterer's commit is now counted.
- [x] **T011** `IndexCommandTests`: thirteen phases in `--json`, the clusterer's five nested and in order, and
  `of which nodes` in the `--verbose` block.

## Phase 4 — The number

- [x] **T012** `dotnet build` and `dotnet test` on the solution in Release: 0 warnings, 1 498 passed.
- [x] **T013** One rebuild with `--verbose`, logged outside the repository, and the five numbers published in
  `spec.md`'s SC-002, `plan.md`'s measurement table and `002`'s `research.md` (D7).
- [x] **T014** Record the finding in `specs/002-code-evidence-index/research.md` as D23, with the numbers beside the
  reasoning and the two candidates it names for the next slice.
- [x] **T015** Round 18 in `specs/000-parity-roadmap.md`: the round section, the rounds table, and `002` T034's open item
  carrying this round's numbers.

## Not in this round

- Any change to what the pass computes: a cheaper partition, a coarser graph, a smaller candidate set, or reusing labels for
  unchanged files.
- The no-op `meta`/open overhead, SC-002's 5 000-file tree, and SC-006's 61×.
