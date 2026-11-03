# Tasks: The Partition Without the Store

**Feature**: `029-modularity-partition` · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Round 20. Ten tasks, one slice. T001 is the probe that chose the design; T002–T004 are the change; T005–T007 are the
evidence that it changed nothing it should not have; T008–T010 are the gates and the record.

- [x] **T001** Three temporary probes on a copy of this repository's index, before any code: the store's
  `LouvainAlgorithm` costs 71.100 s for 12 iterations and 623 communities (its own objective 1.001678, the stored
  partition 0.490230 by the definition); one connected component at a time costs 42.665 s and returns a different
  partition; local moving alone costs 0.0167 s at 0.475347; local moving plus aggregation costs 0.1466 s at 0.655454 with
  603 of the 623 stored communities intact (D1, D2, D3).
- [x] **T002** `Graph/ModularityPartition.cs`: one public method over `int[][]`, a weighted sweep with the staying-put
  baseline, a coarsening that keeps internal weight as a self loop, and a level loop that stops when a level merges
  nothing (FR-003, FR-005, D3, D4).
- [x] **T003** `CommunityAssigner.RankAsync` calls it — `connected` in place of `positions` as the node-to-position map —
  and the `CommunityDetection` namespace leaves the pass (FR-001, FR-008).
- [x] **T004** `IndexPhases.Louvain`'s doc comment names the step rather than the vendor (FR-006).
- [x] **T005** `ModularityPartitionTests`: two triangles apart are two communities; two triangles bridged are still two;
  one edge is one; a node with no neighbour keeps its own label; the same graph twice is the same answer; and a graph
  whose answer only the second phase finds (the two-clique ring, whose single level stops at four) (SC-003, SC-004).
- [x] **T006** `CommunityAssignerTests` keeps its cases unmodified where they still hold: two clusters are two
  communities, two builds store identical labels, an isolated declaration stores an empty label, and the five steps are
  still measured with `louvain` and `degree` at exactly zero on a corpus with no relation (FR-004, FR-006).
- [x] **T007** Delete the three probes (`TempLouvainProbe.cs`, `TempPartitionProbe.cs`, `TempMultiLevelProbe.cs`).
- [x] **T008** `dotnet build` and `dotnet test` on the solution in Release: 0 warnings, the suite green, and the new cases
  counted against 1 502: 1 508 passed, 0 failed, 0 skipped.
- [x] **T009** One `index --rebuild --verbose` and one `index --verbose` over a changed tree, logged outside the
  repository, and the five steps published beside round 19's `louvain` 1 m 8 s (SC-001, SC-002, NFR-001, NFR-002).
- [x] **T010** Record the decision in `specs/002-code-evidence-index/research.md` as D25 with the probe and both runs
  beside it, and round 20 in `specs/000-parity-roadmap.md`: the round section, the rounds table, the numbering note, and
  `002` T034's open item.

## Not in this round

- `degree` (0.0 s). It stays the store's `DegreeCentrality`, which is linear and is not the subject.
- The graph's definition: every relation, undirected, unweighted. Weighting `calls` above `doc_mentions` is a decision
  about what a label means, and this round is not taking it.
- A resolution parameter, a sweep budget, or any knob: one behaviour, one number.
- The write-back (1.8 s), the commit count, the checkpoint's size, the no-op `meta`/open overhead (5.5–6.0 s a turn),
  SC-002's 5 000-file tree and SC-006's 61× — `002` T034 stays open for those.
