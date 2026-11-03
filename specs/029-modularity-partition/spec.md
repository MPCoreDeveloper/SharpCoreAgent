# Feature Specification: The Partition Without the Store

**Feature Branch**: `029-modularity-partition`

**Created**: 2026-11-03

**Status**: Accepted 2026-11-03 (constitution 1.2.0). Round 20 of `specs/000-parity-roadmap.md`. It takes the lever round
19 left behind — `louvain`, 1 m 8 s and 58 % of a rebuild — and answers the question round 18 asked of it: is the
partition expensive, or is this implementation?

**Input**: User description: "`028`'s next slice: `louvain` is 1 m 8 s of a 1 m 56 s rebuild and the fixed charge on every
writing run, and it is in third-party code."

## Why

Round 19 left one number standing, and it is now the run:

> `communities` 1 m 10 s (61 %): `nodes` 0.0 s, `adjacency` 0.0 s, `louvain` 1 m 8 s, `degree` 0.0 s, `assign` 1.8 s.

`louvain` is one call to one method the store ships, over a graph of 4 123 declarations and 10 744 undirected
relations. Twelve seconds per thousand nodes and ten edges is not what a sparse graph costs: at that edge-to-node ratio
a partition is linear work, and the whole graph fits in a corner of one megabyte. So the question this round asks is the
one round 19 asked of `assign` and answered with a probe: is the work the graph, or is it the arithmetic around it?

### The measurement, before the code

Three temporary probes, each run twice on a copy of this repository's own index, deleted with the round
(`specs/002-code-evidence-index/research.md` D25 keeps the numbers):

| probe | measured |
|---|---|
| the graph itself | 7 311 declarations, 10 840 relation rows (calls 10 095, doc_mentions 635, implements 89, inherits 21), **4 123** with at least one relation, 10 744 distinct undirected edges, 3 188 with none |
| the store's `LouvainAlgorithm` on all of it | **71.100 s**, 12 iterations, 623 communities, its own objective reports 1.001678 |
| the same algorithm, one connected component at a time | 42.665 s — and a **different** partition (618 communities), so decomposition is not free |

The first two rows together are the finding. `LouvainAlgorithm.ExecuteAsync` recomputes `CalculateModularity` — a LINQ
`GroupBy` over every node, then a scan of every adjacency list — once for the starting partition, once per iteration,
and **once per candidate move**. This graph offers 25 611 candidate moves per sweep (each node, each neighbouring
community, plus staying put), so twelve sweeps is roughly 307 000 full-graph scans for 4 123 nodes: the algorithm is
quadratic in the node count and allocates a dictionary per step. The 71 s is the scan, not the graph.

The third number is the one that closes the obvious door. The graph is one component for 97 % of its members — 3 999 of
4 123 — so splitting the work by component buys 1.7×, changes the partition, and would leave 30 s standing. That is not
the lever.

| probe | measured |
|---|---|
| local moving only, our arithmetic | 0.0167 s, 7 sweeps, 651 communities, **modularity 0.475347** |
| local moving plus aggregation, our arithmetic | **0.1466 s**, 5 levels, 59 communities, **modularity 0.655454** |
| the partition the index stores today, scored by the same definition | 0.490230 |
| how much of today's partition survives | 603 of the 623 stored communities sit inside one new community; the largest grows from 196 to 494; the run is deterministic on repeat |

So one level of modularity local moving answers in a sixtieth of a second and lands 3 % *below* what the store's broken
objective produced by luck. Adding the algorithm's second phase — communities become the next level's nodes, until a
level merges nothing — costs another tenth of a second and lands 34 % **above** it, with the old clusters intact inside
the new ones. Louvain is the sweep plus the hierarchy; the store shipped the sweep, called it by the hierarchy's name,
and paid a whole-graph scan for every candidate it considered.

### What round 19 left open, and why the two candidates it named are not taken

Round 19's open item says making `louvain` smaller means "a coarser graph or a bounded candidate set — a decision about
label stability". Both are approximations, and both are now measurably worse than the third option:

- **A coarser graph** — cluster files (761) instead of declarations (4 123) — is quadratic-cheaper by the ratio of the
  node counts, so about 29× and 2.4 s, and it changes what a label means: "these two files are related" instead of
  "these two declarations are". It also throws away the 4 123 fine relations Louvain is good at.
- **A bounded candidate set** — test the four best neighbour communities instead of all 6.2 — divides the 307 000 scans
  by 1.5 and keeps the same answer's quality only by accident.
- **The third option** is the same objective, computed correctly: no approximation, no coarser graph, no bounded set, and
  it is 485× cheaper than the store's call while scoring 34 % higher on the definition both of them claim to optimise.

That is what this round takes. The label stability question round 19 deferred is answered rather than dodged: the
labels stay a function of the graph alone, the numbering rule does not move, and the clusters that exist today survive
whole inside the new ones. What moves is the granularity — 623 micro-clusters of 6.3 declarations become 59 clusters of
70 — and that is a change to what `communities` reports, so it is stated as one (SC-005) rather than hidden.


## User Scenarios & Testing *(mandatory)*

### User Story 1 — A writing run stops paying for the whole graph (P1)

A developer changes one file and runs `sharpcoreagent index --verbose`. Today the run costs what a full rebuild's
clusterer costs, because `communities` is entered whenever anything was written and `louvain` re-partitions the whole
graph from scratch: 1 m 8 s for a file that added nine edges. After this round the same run reports `louvain` in
milliseconds, and `communities` is whatever the write-back of 7 311 rows costs.

**Why this priority**: it is the slice. The clusterer is no longer the clusterer's cost; it is one call to one algorithm,
and that call is the fixed charge on every run that writes anything.

**Independent Test**: one `index` run over a changed copy of this repository's tree reports `louvain` below one second and
a `communities` phase below five, against round 19's 1 m 8 s and 1 m 10 s.

**Acceptance Scenarios**:

1. **Given** an index whose graph has 4 123 connected declarations, **when** a build re-partitions it, **then** the
   `louvain` step reports well under a second and the whole `communities` phase is dominated by the write-back.
2. **Given** a rebuild of the same tree, **when** it is timed end to end, **then** the run is what the other phases cost
   — the clusterer is no longer the largest line in the report.
3. **Given** a corpus with no relations, **when** a build runs, **then** `louvain` and `degree` stay at exactly zero: the
   partition still only runs when there is something to partition.

### User Story 2 — A label still names the same cluster (P2)

The stored label is what `search` and `traverse` read as the community signal. Two declarations that were in one cluster
yesterday must not be in unrelated clusters tomorrow, and the same corpus must produce the same labels on two builds,
or an index would answer differently depending on when it was built.

**Why this priority**: second, and it is the reason this is a spec rather than a patch. Round 19 deferred the question
of what a label means; this round answers it — the meaning does not move, the arithmetic that finds it does.

**Independent Test**: on this repository's index, 603 of the 623 previously stored communities sit inside one new
community, the numbering rule is unchanged, and a build twice in a row stores identical labels.

**Acceptance Scenarios**:

1. **Given** a corpus of two clusters that never reference each other, **when** a build runs, **then** it stores exactly
   two communities, as round 19 did.
2. **Given** the same corpus built twice, **when** the two label columns are compared, **then** they are identical, value
   for value, and identical again in a second process.
3. **Given** a declaration that was in one community before this round, **when** the new partition is read, **then** it
   is in one community still — clusters may merge, they do not scatter: 603 of 623 stay whole.
4. **Given** a declaration with no relation at all, **when** the pass runs, **then** its label is empty, exactly as
   before: it is in no cluster, and being alone is not a cluster.

### User Story 3 — The step is arithmetic this repository can read (P3)

`louvain` is the one step in the build whose cost nobody here could explain, because the code was in a package. After
this round the step is a type in `SharpCoreAgent.Indexing`, four methods long, with the objective it optimises written
in the file and a test that checks the partition against that objective.

**Why this priority**: third, but it is what makes the first two durable: the next round that needs a different
resolution, a weight, or a bounded sweep has somewhere to put it, and a graph of 50 000 declarations does not send
anybody back to a package's issue tracker.

**Independent Test**: `ModularityPartition` alone, over hand-built graphs, returns the partition the definition demands;
`CommunityAssigner` names no type from the store's community detection namespace.

**Acceptance Scenarios**:

1. **Given** two triangles joined by nothing, **when** the partition runs, **then** it returns two communities.
2. **Given** two triangles joined by a single edge, **when** the partition runs, **then** it still returns two: the
   bridge is cheaper to cut than to keep.
3. **Given** any graph, **when** the partition runs twice, **then** the two answers are equal, and no step of it depends
   on the iteration order of a dictionary.

### Edge Cases

- **A graph with one edge.** Two nodes, one relation: one community, and the pass writes it. Nothing about a level with
  fewer nodes than communities has to be special-cased, but the loop must stop rather than coarsen to nothing.
- **A declaration with no relation.** It never enters the graph — round 18 decided that, and this round inherits it — so
  its label is empty and no level ever sees it.
- **A component of two.** The coarsened graph has one node and no neighbours; the next level merges nothing, so the loop
  stops on the level after the merge rather than dividing by a zero edge count.
- **A hub.** This index's largest degree is 904. The sweep's cost is its degree, not the graph's size, and the candidate
  list is iterated in ascending community order so a hub's ties break the same way twice.
- **A cancelled build.** The pass still writes in one batch and still cancels it; a partition that is computed and then
  not written changes nothing, which is what round 19 promised for the write.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The `louvain` step MUST NOT call `LouvainAlgorithm`, or any other type from the store's community
  detection namespace, when a build partitions its graph.
- **FR-002**: It MUST partition the graph the pass already builds: every declaration with at least one relation, every
  relation the `edges` table holds, undirected, unweighted, with nodes in `key` order so the input does not depend on
  the order the files were written.
- **FR-003**: It MUST optimise modularity as defined —
  `Q = Σ_c [l_c/m − (d_c/2m)²]` — accepting a node move only when it raises that number, and repeating at the coarser
  level until a level merges nothing.
- **FR-004**: Labels MUST keep their shape: `c` plus the community's rank by the ordinal position of its ordinally-first
  member, and an empty label for a declaration with no relation at all.
- **FR-005**: The same graph MUST produce the same labels in the same process and in the next one. No step may depend on
  the iteration order of a `Dictionary`, on a hash code, or on wall-clock time.
- **FR-006**: The step MUST still be measured as `louvain`, nested under `communities`, and MUST still be exactly zero
  when the corpus has no relation to partition.
- **FR-007**: Everything around it MUST NOT move: the schema, the columns, the indexes, the flags, `--json`, the report,
  the commit count, `DegreeCentrality`, and the write-back round 19 landed.
- **FR-008**: `CommunityAssigner` MUST name the store only for what the store still does — the handle, the rows, the
  degree centrality — and the partition MUST be a type in `SharpCoreAgent.Indexing` with no vendor type in its signature.

### Non-Functional Requirements

- **NFR-001**: `louvain` on this repository's index (4 123 connected declarations, 10 744 edges) MUST fall from round
  19's 1 m 8 s to below one second, measured on the same machine and published.
- **NFR-002**: `communities` MUST fall from 1 m 10 s to below 5 s, and a rebuild of this repository MUST fall from
  1 m 56 s to below a minute.
- **NFR-003**: The partition MUST score at least the modularity of the partition it replaces on this index — 0.490230 by
  the definition — so that "faster" is not bought with a worse answer.
- **NFR-004**: The suite MUST stay green, and the new cases MUST be the ones that fail if the partition loses its
  second phase, its determinism, or its "staying put" baseline.

### Key Entities

- **The graph**: the connected declarations and their relations, addressed by position, built once per pass and shared
  with `DegreeCentrality`.
- **The level**: one local-moving sweep plus the coarsening that follows it. Communities become nodes, weights are
  summed, internal weight becomes a self loop.
- **The partition**: one label per node, renumbered by the pass into `c0…cN` by first member. It is the only thing the
  step returns.

## Success Criteria *(mandatory)*

- **SC-001**: A rebuild reports `louvain` below 1 s, against round 19's 1 m 8 s, on the same index and machine.
- **SC-002**: A writing run over a changed tree reports `communities` below 5 s, against round 19's 1 m 10 s fixed
  charge, and the run ends below a minute.
- **SC-003**: Two builds of the same corpus store identical labels, proven by a test rather than by the arithmetic.
- **SC-004**: The two-cluster corpus still stores exactly two communities, and a declaration with no relation still
  stores an empty label.
- **SC-005**: The partition this index stores scores at least 0.490230 modularity, and `communities` reports the new
  granularity honestly: 59 clusters of 70 declarations where round 19 stored 623 of 6.3, with 603 of the old ones intact
  inside the new.
- **SC-006**: No type from the store's community detection namespace is named in `louvain`'s path, and the suite is green
  with the new cases counted.

## Assumptions

- The 71.1 s is the modularity recompute rather than the graph's size, because the same algorithm over 37 components
  instead of one graph costs 1.7× less while touching the same edges; if the cost were per edge, decomposition would
  have changed nothing.
- A local optimum of the definition is a good enough answer for a community signal, because the partition replaces 623
  clusters of 6.3 declarations with 59 of 70 and 97 % of the old clusters survive inside the new ones.
- Five levels is the natural depth of this graph, not a tuned constant: the loop stops when a level merges nothing, and
  the sweep cap is a guard rather than a parameter.

## Out of Scope

- `degree` (0.0 s), and the graph itself: every relation, unweighted, undirected, exactly as round 18 defined the input.
- The no-op `meta`/open overhead (5.5–6.0 s a turn), the checkpoint's own size, SC-002's 5 000-file tree and SC-006's 61×
  in `002` T034 — round 19 left those, and this round does not reach for them.
- A resolution parameter, edge weights, a bounded candidate set, or any knob that changes what a label means. The round
  has one behaviour.
- The `symbols.community` index, the write-back, the numbering of a build's commits, and `--json`.
