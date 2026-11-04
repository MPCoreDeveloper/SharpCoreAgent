# Tasks: The Rows That Already Said It

**Feature**: `032-write-only-what-moved` · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Round 23. Eight tasks, one slice. T001 is the probe that priced the writing run and found where the round could stand;
T002–T004 are the change, including the shape it had to be talked out of; T005–T006 are the evidence; T007–T008 are the
gates and the record.

- [x] **T001** Probes on this repository's index, before any code: one document changed — **12.2 s** wall, `commits` 3,
  `write` 7.2 s, `assign` 2.4 s, `meta` 2.0 s, `commit` 11.2 s; the same change reverted — 11.9 s and 10.9 s of commit;
  a 103-line document against a 700-line source file — `write` **7.2 s** and **7.6 s**; the store **73.6 → 110.4 MiB**
  across those four runs (D4, spec's table).
- [x] **T002** `CommunityAssigner.SelectSymbols` selects `community` and `centrality`; `SymbolNode` carries the stored
  label and degree; `ReadSymbols` maps them and documents why the answer travels with the address; `IndexSql.AsDouble`
  reads a `REAL` column as `AsLong` reads an integer one (D1, FR-001).
- [x] **T003** `Write` walks the declarations into `moves` — a row joins it when the label differs ordinally or the degree
  differs as a number — and returns 0 without calling `BeginBatchUpdate` when `moves` is empty; `SameDegree` compares the
  bits, so `S1244` is answered by the shape of the comparison rather than by a suppression (D3, FR-002).
- [x] **T004** The comparison is made in the reader's form: `labels[index] ?? string.Empty` against the empty text an
  unclustered row reads back as, and both degrees through `AsDouble`, which turns `DBNull` into `0`. The first version
  compared `null` with `""` and rewrote every isolated declaration, which `ProbeTmpTests` showed as `second=1` and a
  rerun showed as `second=0` (D2, FR-003).
- [x] **T005** `CommunityAssignerTests`: `APassThatFindsTheSameAnswersWritesNothing` (a document changes; two commits —
  the file and `meta` — and the stored columns unchanged) and `AStaleLabelIsTakenBackEvenWhenTheGraphHasNotMoved` (a
  hand-written `c99` on an isolated declaration, a document change, three commits, the empty label back), with
  `BuildReport`, `WriteAsync`, `WriteCommunity` and `ReadSymbols(IDatabase)` beside them (D5, SC-002, SC-003).
- [x] **T006** Measure it after `index --compact` (110.4 → 73.8 MiB, the size the baseline was taken at): one changed
  file costs **9.2 s** and **2 commits** with `assign` **0.0 s**, against 12.2 s, 3 commits and 2.4 s. A run whose graph
  did move — four files, two new declarations — ran `communities` 2.3 s with `assign` 2.1 s and `commits` 4, so the pass
  still writes when it has something to write. A refresh that changes nothing is **0.5–0.7 s** with `commit` 0, as
  round 22 left it (SC-001, SC-004, SC-005).
- [x] **T007** `dotnet build -c Release` at **0 warnings** and `dotnet test` green: **1 520 of 1 520**, the 1 518 of round
  22 plus the two new cases, with `CommunityAssignerTests` at 7 (SC-006).
- [x] **T008** Record it: this spec, plan and task list; round 23 in `specs/000-parity-roadmap.md` (the rounds table and
  the round section) with round 22's commit named at last; and the note under `002` T034, where the floor that is left now
  reads 6.7 s of write commit and 2.1 s of `meta` rather than 12.8 s spread over three of them (SC-007).

## Not in this round

- The write batch's 6.7 s and `meta`'s 2.1 s: both carry a row that changed, and both are whole-table index maintenance
  inside the store's commit — measured as neither payload nor file size (D4).
- Coalescing the run's two remaining commits into one. That is the next lever, and it has to answer `026`'s failure-unit
  argument before the boundary moves (D6).
- The store growing 33 % across four writing runs of one changed file: `index --compact` reclaims it, nothing compacts
  automatically, and round 21 measured the reclaim.
- `--prune` that removes a file (10.1 s), SC-002's 5 000-file tree, SC-006's ratio and SC-003's p95 — `002` T034.
- Any new persisted state, and any growth of `HeuristicSyntaxFactsExtractor`.
