# Tasks: The Write-Back Without a Parser

**Feature**: `028-row-addressed-symbol-writes` · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Round 19. Nine tasks, one slice. T001–T002 are the change; T003–T006 are the evidence that it changed nothing it should
not have; T007–T009 are the gates and the record.

- [x] **T001** `CommunityAssigner.Write` writes both columns through `IndexSql.Update` by primary key, one dictionary per
  row, inside the same batch, with both `commit` measurements and the single counted commit untouched (D1, D2, D4).
- [x] **T002** Delete `UpdateSymbol`: no `UPDATE` statement remains in `src/` (FR-006, SC-005).
- [x] **T003** `IndexSqlUpdateTests`: a row-addressed update inside a batch is stored by `EndBatchUpdate`, is discarded by
  `CancelBatchUpdate`, and changes only the columns it names (D3).
- [x] **T004** `CommunityAssignerTests`: a build over the two-cluster corpus leaves `name`, `qualified_name`, `kind`,
  `doc`, `signature` and the line range of every declaration exactly as the extraction wrote them (FR-005, D3).
- [x] **T005** The existing pass tests keep passing unmodified: the two labels, the identical-labels-across-builds case, the
  five nonzero steps, the centrality ranking, and `commits` unchanged (FR-002, FR-003, NFR-002).
- [x] **T006** Delete the temporary probe (`TempAssignWriteProbe.cs`), which measured 12 875–14 483 ms for 3 001
  statements against 86–143 ms for the same rows by primary key.
- [x] **T007** `dotnet build` and `dotnet test` on the solution in Release: 0 warnings, the suite green, and the new cases
  counted against 1 498: 1 502 passed, 0 failed, 0 skipped.
- [x] **T008** One `index --rebuild --verbose` on this repository, logged outside the repository, and the five steps
  published beside round 18's `assign` 1 m 21 s (SC-001, NFR-001): `assign` 1.8 s, `communities` 1 m 10 s, the run 1 m 56 s, 27 commits.
- [x] **T009** Record the decision in `specs/002-code-evidence-index/research.md` as D24 with both measurements beside it,
  and round 19 in `specs/000-parity-roadmap.md`: the round section, the rounds table, the numbering note, and `002` T034's
  open item.

## Not in this round

- `louvain` (1 m 8 s). It is now the phase and the run, it is in third-party code, and making it smaller means a coarser
  graph or a bounded candidate set — a decision about what a label means, and therefore a spec of its own.
- The no-op `meta`/open overhead (5.5 s a turn, measured again at 6.0 s), the checkpoint's own size, SC-002's 5,000-file
  tree and SC-006's 61×.
- Dropping `idx_symbols_community` for the residual 1.8 s. It would change the plan of every query that reads a community,
  and the probe says the residual is not where the money is.

