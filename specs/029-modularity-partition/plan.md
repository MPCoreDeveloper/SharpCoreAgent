# Implementation Plan: The Partition Without the Store

**Feature**: `029-modularity-partition` · **Spec**: [spec.md](spec.md) · **Tasks**: [tasks.md](tasks.md)

Round 20. One slice: the `louvain` step stops calling the store's `LouvainAlgorithm` and partitions with a modularity
implementation this repository owns — the same nodes, the same edges, the same numbering rule, 485× less time, and a
partition that scores 34 % higher on the definition both of them claim to optimise.

## Summary

`CommunityAssigner` keeps its shape: `nodes`, `adjacency`, `louvain`, `degree`, `assign`, one batch, one counted commit.
Inside `louvain`, `RankAsync` stops awaiting `new LouvainAlgorithm().ExecuteAsync(graph, …)` and calls
`ModularityPartition.Nodes(graph.AdjacencyList, cancellationToken)` — a type in `SharpCoreAgent.Indexing` that runs the
algorithm's two phases over the adjacency list the pass already built.

Two `using` lines and one call site move. `GraphData`, `DegreeCentrality` and the graph's construction stay, because the
degree step is still the store's and is not the subject. Nothing else in `src/` changes: no schema, no column, no index,
no flag, no report field, no commit count.

The one thing that does change is the granularity of the answer — 623 clusters of 6.3 declarations become 59 of 70 on
this index — and D3 is why that is a win rather than a regression.

## Decisions

### D1 — The 71 s is the algorithm's arithmetic, not the graph's size

`LouvainAlgorithm.ExecuteAsync` (the package's own source is in the sibling `SharpCoreDB` checkout,
`src/SharpCoreDB.Graph.Advanced/CommunityDetection/LouvainAlgorithm.cs`) calls `CalculateModularity` — a LINQ `GroupBy`
over every node followed by a scan of every adjacency list — once for the starting partition, once per iteration, and
**once per candidate move**, restoring the node afterwards in between. This graph offers 25 611 candidate moves per
sweep over 4 123 nodes; twelve sweeps is about 307 000 full-graph scans, each allocating a dictionary of 4 123 entries.
The probe confirms the diagnosis by taking the graph apart: the same algorithm over 37 connected components costs
42.665 s instead of 71.100 s and returns a different partition, so the cost is quadratic in the node count rather than
linear in the edges.

The second thing the probe found is the one that decides the round. The implementation's objective reports 1.001678 for
its own partition. Modularity cannot exceed 1: `CalculateModularity` counts every internal edge twice — once from each
endpoint — so what it maximises is `2·(internal fraction) − Σ(d_c/2m)²`, not `Σ[l_c/m − (d_c/2m)²]`. Scored by the
definition, the partition it stored scores **0.490230**.

### D2 — The replacement is the same objective computed properly, not a coarser graph

Round 19 predicted that `louvain` would have to be bought with a decision about label stability — a coarser graph or a
bounded candidate set. Measured against the third option, both are worse:

| option | cost on this graph | what it does to the answer |
|---|---|---|
| coarser graph, files (761) instead of declarations (4 123) | ≈ 2.4 s, by the algorithm's own quadratic | a label becomes "these two files are related" |
| bounded candidate set, 4 of 6.2 | ≈ 48 s, 1.5× | the same answer's quality, by accident |
| one connected component at a time | 42.665 s, 1.7× | a different partition, 618 against 623 |
| **the same objective, linear per sweep, with aggregation** | **0.1466 s** | 59 communities, modularity **0.655454** |

Coarsening throws away the 4 123 fine relations the graph was built to express; bounding the candidates keeps the
quadratic scan and divides it by one and a half. Neither is worth a whole spec. The fourth row is not an approximation at
all: it is the algorithm Louvain names minus the per-candidate recompute — the gain of a move is computed from the
weights it already has to sum, in `O(degree)` instead of `O(n + m)`.

### D3 — The second phase is the round, not decoration

A single level of local moving was measured first, because it is the smaller change: 0.0167 s, 7 sweeps, 651
communities, modularity **0.475347**. That is 3 % *below* the 0.490230 the store's partition scores by the definition —
so doing the arithmetic correctly but stopping at one level would have shipped a slower-looking-for-an-excuse answer, and
the round would have had to argue that 0.015 of modularity is a fair price for 34 s.

It is not necessary to argue it. Adding the algorithm's second phase — communities become the next level's nodes until a
level merges nothing — costs **0.13 s more** and lands at **0.655454**, 34 % above what the round replaces. On this
index it takes five levels, the partition falls to 59 communities of 70 declarations, and **603 of the 623 stored
communities sit whole inside a new one**, the largest growing from 196 to 494.

So the round ships the hierarchy, and the honesty it owes the reader is not "we accepted a worse partition" but "the
partition is now a coarser, better-scoring one, and the numbers say which clusters merged".

### D4 — Determinism by construction, not by luck

Three rules, each one measured or forced:

1. **The staying-put baseline.** A node moves only when the best neighbouring community beats the community it is in; the
   first probe compared candidates against zero instead, and did not converge — 64 sweeps of trading nodes back and forth,
   modularity 0.379, still moving. The baseline is what makes a sweep finite.
2. **Ascending candidate order.** The candidate communities for a node are visited in ascending id, never in
   `Dictionary` iteration order, so an exact tie breaks the same way in every process.
3. **Stop on no progress.** The level loop ends when a level merges nothing (`next >= local.Length`), so the graph's own
   structure decides the depth — five levels here — and the caps (8 levels, 64 sweeps) are guards against a graph that
   never settles rather than parameters anybody tunes.

Node order is the pass's: `key` order, then `id`; neighbours ascending; that is what makes two builds of the same corpus
store identical labels, which is a test rather than a claim (SC-003).

### D5 — What this round does not do

- **`degree` (0.0 s).** `DegreeCentrality` stays the store's. It is linear, it is a sixteenth of a second on a bad day,
  and replacing it would be a second argument in the same round.
- **The graph's definition.** Every relation, undirected, unweighted — round 18's rule, untouched. Weighting `calls`
  above `doc_mentions` would change what a label means, which is the decision this round is *not* taking.
- **A resolution parameter, a sweep budget, or any knob.** One behaviour, one number; a knob would make two indexes of
  the same corpus incomparable unless the knob is recorded, and nothing here records it.
- **The write-back (1.8 s), the commit count, the checkpoint, the no-op `meta`/open overhead (5.5–6.0 s a turn), SC-002's
  5 000-file tree and SC-006's 61×.** Those are `002` T034's, and they stay open.

## Approach

1. `src/SharpCoreAgent.Indexing/Graph/ModularityPartition.cs` — `internal static class ModularityPartition`, one public
   method `Nodes(int[][] adjacency, CancellationToken cancellationToken) → int[]` and three private steps: `Sweep`
   (local moving on a weighted graph with self loops), `Coarsen` (communities into nodes, weights summed, internal weight
   into a self loop), and the level loop that composes the mapping back to the original nodes.
2. `CommunityAssigner.RankAsync` calls it, with `connected` in place of `positions` as the node-to-position map, and
   loses `using SharpCoreDB.Graph.Advanced.CommunityDetection;`. The method's remark says what it now does and why the
   objective is written down rather than delegated.
3. `IndexPhases.Louvain`'s doc comment stops claiming the store partitions: it names the step, not the vendor.
4. `ModularityPartitionTests` — the definition's cases: two triangles apart, two triangles bridged, one edge, an isolated
   node, a repeated run, and a graph whose answer only the second phase finds.
5. `CommunityAssignerTests` — the pass-level case: the five steps are still measured, and the two-cluster corpus is still
   two communities.
6. `dotnet build` and `dotnet test` in Release; the temporary probes deleted (`TempLouvainProbe`, `TempPartitionProbe`,
   `TempMultiLevelProbe`).
7. One `index --rebuild --verbose` and one `index --verbose` over a changed tree on this repository, both logged outside
   the repository, and the numbers published beside round 19's.
8. `specs/002-code-evidence-index/research.md` gains D25; `specs/000-parity-roadmap.md` gains round 20.

## Measured

The probes, before the change, on a copy of this repository's own index — 7 311 declarations, 10 840 relation rows,
4 123 connected, 10 744 distinct undirected edges, 3 188 with no relation, largest degree 904:

| probe | measured |
|---|---|
| the store's `LouvainAlgorithm`, whole graph | 71.100 s, 12 iterations, 623 communities, its own objective 1.001678 |
| the same, one connected component at a time (37) | 42.665 s, 618 communities, **not** the same partition |
| the stored partition, scored by the definition | 0.490230 |
| ours, local moving only | 0.0167 s, 7 sweeps, 651 communities, modularity 0.475347 |
| **ours, local moving plus aggregation** | **0.1466 s**, 5 levels, 59 communities, modularity **0.655454** |
| ours, run twice | identical |
| how much of the stored partition survives ours | 603 of 623 communities whole; largest 196 → 494 |

The rebuild, after it — same tree plus this round's five files (766 files, 14 348 passages, 7 330 symbols, 10 867 edges,
58 communities, 787.9 MiB; run twice, identical; `--prune` reproduces the same file):

| step | round 19 | round 20 |
|---|---|---|
| `nodes` | 0.0 s | 0.0 s |
| `adjacency` | 0.0 s | 0.0 s |
| `louvain` | 1 m 8 s | **0.1 s** |
| `degree` | 0.0 s | 0.0 s |
| `assign` | 1.8 s | 2.4 s |
| `communities` | 1 m 10 s | **2.6 s** |
| `write` | ~40 s | 44.1 s |
| the run | 1 m 56 s | **55.6 s** |
| commits | 27 | 27 |
| communities stored | 623 | **58** |

And the run that round 19 named as the reason all of this mattered — `index --verbose` over a tree where one file had
changed: **11.3 s** wall (11.5 s with the process), `communities` **2.2 s** of it (`louvain` 0.1 s, `assign` 2.0 s,
`commit` 7.0 s), against round 19's 1 m 10 s clusterer for the same shape of run. A run that changes nothing costs
**5.8 s**, unchanged from D24's 5.5–6.0 s.

NFR-001 asked for `louvain` below 1 s: **0.1 s**. NFR-002 asked for `communities` below 5 s and the run below a minute:
**2.6 s** and **55.6 s**. NFR-003 asked the new partition not to score below the one it replaces: **0.655454** against
0.490230, measured on the same definition. `index` reports `state fresh`; `index --verify` finds 766 rows for 766 files, 0
hash mismatches, 0 orphans; the suite is 1,508 passed, 0 failed, 0 skipped against 1,502.

## Complexity Tracking

One thing is added and one is removed, and they are the same thing: this repository now owns a partition algorithm and no
longer calls the store's. The added type is arithmetic over `int[][]` — no dependency, no vendor type, no configuration —
and it is bought by two facts the round measured rather than asserted: the store's implementation costs 71.100 s on a
graph whose partition is 0.15 s of work, and it optimises a quantity it calls modularity that differs from modularity by
a factor of two on the internal-edge term.
