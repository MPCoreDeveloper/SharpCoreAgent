# Feature Specification: The Clusterer's Own Steps

**Feature Branch**: `027-community-pass-steps`

**Created**: 2026-11-02

**Status**: Accepted 2026-11-02 (constitution 1.2.0). Round 18 of `specs/000-parity-roadmap.md`. It takes the second of the
three levers `002`'s `research.md` D21 named — the half of it that has to come first: saying which of the clusterer's
steps the two minutes is.

**Input**: User description: "`026`'s next slice: `communities` is 74 % of a rebuild and a fixed charge per writing
run, and nobody knows which part of it costs the two minutes."

## Why

Round 17 landed a checkpoint and the write phase fell 25×. What it left behind is a run whose cost has one owner:

> `communities` is now three quarters of a rebuild and it is a fixed charge per *writing* run, so the next slice is D21's
> second lever rather than more of this one. — `002` `research.md` D22

That is a phase, not an answer. It is one line in a report and one method in the code, and everything below that line is
a guess: reading 7 305 declarations, reading 10 806 relations, building an adjacency list, running Louvain, running
degree centrality, or issuing one `UPDATE` per declaration. Five plausible suspects spanning two orders of magnitude,
and D21's own lesson applies to all five:

> The commit is the write. … whatever the rows cost, it is spent in `EndBatchUpdate` rather than in the code between the
> batch's two ends.

That lesson is not a fact about this phase — it is a warning that the number a step reports may not be the code the step
looks like. The write phase looked like extraction and was a commit. `communities` may look like Louvain and be a commit,
or a table scan, or a statement loop. Guessing wrong costs an hour of optimisation aimed at 2 % of the run.

So this round answers the question instead of acting on it, exactly as D21 did for the build as a whole. The whole run
became eight numbers; now one of those numbers becomes five.

### The measurement the phase already had, and what it hides

`026`'s published run and this round's first run agree on the shape and disagree on nothing that matters:

| | round 17 (`acf125e`) | this round's rebuild |
|---|---|---|
| the run | 2 m 53 s | 3 m 2 s |
| `communities` | 2 m 8 s (74 %) | 2 m 15 s (74 %) |
| commits | 26 | 27 |

The index grew between the two (740 files, 14 200 passages, 525.2 MiB → 740 files, 14 231 passages, 7 305 symbols,
10 806 edges, 645 communities, 525.2 MiB), and the phase grew with it. Two runs of one phase, both opaque.

### What this round also fixes: a commit nobody counted

While splitting the pass it becomes clear that one of its steps asks the store to commit and nobody is counting it.
`CommunityAssigner` writes both columns through its own `BeginBatchUpdate`/`EndBatchUpdate` pair rather than through
`IndexFileWriter`, so that commit is inside the phase's wall clock and outside `commits`, and outside the `commit` line —
two numbers round 17 had just made trustworthy. A build reported 26 commits while making 27.

That is the same class of error the previous round spent its length avoiding: a cost that is real, paid, and missing from
the report. So the clusterer's commit is charged to `commit` at both ends and counted with the others, which makes
`commits` every commit the build asked for and the `commit` line every second the store charged for one.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — A developer can see which step the two minutes is (P1)

A developer runs `sharpcoreagent index --rebuild --verbose` and reads the phase block. Before this round one line says
`communities 2 m 15 s 74 %` and stops. After it, five lines under that one say what the phase is made of, and the next
hour of work has a target instead of a hypothesis.

**Why this priority**: it is the whole slice. Every lever this program has left — a cheaper partition, a cheaper write, a
cheaper checkpoint — is priced by whether it moves one of these five numbers.

**Independent Test**: a rebuild of a corpus with two clusters reports all five steps as nonzero; a corpus with no
relations reports `louvain` and `degree` as zero and the other three as nonzero. Both pass without reading a terminal.

**Acceptance Scenarios**:

1. **Given** an index of any size, **when** a build writes or removes anything, **then** `communities` is reported and the
   five steps under it are reported too, in a fixed order, each with wall clock and processor time.
2. **Given** an index whose graph has nothing connected to anything, **when** a build runs, **then** `louvain` and
   `degree` report zero rather than a plausible small number: they did not run, and a report that rounded that up would be
   inventing work.
3. **Given** a build that wrote nothing and removed nothing, **when** it runs, **then** neither the phase nor its steps
   report anything but zero, because the pass is skipped as it is today.

### User Story 2 — The commit count is the number of commits (P2)

A developer compares `commits` against the `commit` line to see what the store charged. Before this round the two disagree
by one on every writing run, because the clusterer's batch was invisible to both.

**Why this priority**: second, because it is small and because it is the difference between a number that can be trusted
in an argument and one that has to be re-derived. It also protects the next round: `assign` cannot be priced if its commit
is reported as statement work.

**Independent Test**: a five-file build reports one commit per checkpoint plus one each for the edges, the columns and
`meta`; a build that writes nothing still reports one.

**Acceptance Scenarios**:

1. **Given** a build that writes files, **when** the report is read, **then** `commits` includes the clusterer's batch.
2. **Given** a build whose budget expires before its first file, **when** the report is read, **then** `commits` is one,
   because the clusterer did not run.

### Edge Cases

- **A corpus with one connected declaration.** The pass runs, the graph has a single node, and neither algorithm is handed
  it: `louvain` and `degree` are zero and `assign` is not. The split must say that rather than fail somewhere in the
  middle.
- **A pass that writes rows and then fails.** The batch is cancelled, no commit is counted, and the columns keep their
  previous values — the promise `CommunityAssigner` already made, now with a count that agrees with it.
- **A report read by a script.** The steps are rows of the same `phases` array as everything else, with `nested: true`, so
  a script that already walks that array sees them without a second document.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A build MUST report five steps inside `communities` — `nodes`, `adjacency`, `louvain`, `degree` and
  `assign`, in that order — each with wall clock and processor time.
- **FR-002**: Each of the five MUST be marked nested, because its time is already inside `communities`, and a nested step
  MUST NOT take a share of the run.
- **FR-003**: `nodes` MUST cover reading the declarations and building the id-to-position map; `adjacency` MUST cover
  reading the relations, building the neighbour lists and renumbering them into the graph the algorithms take; `louvain`
  MUST cover the partition and the numbering that makes its labels reproducible; `degree` MUST cover the centrality
  metric; `assign` MUST cover writing both columns back.
- **FR-004**: The clusterer's batch MUST be charged to `commit` at both ends and counted in `commits`, like every other
  commit a build makes.
- **FR-005**: Nothing observable may change: the rows, labels, centrality values, counters, hashes and `--json` document a
  build leaves are the same as before this round.
- **FR-006**: The measured run MUST be published — the five numbers beside the phase's single line — because the point of
  the round is a number nobody had.

### Non-Functional Requirements

- **NFR-001**: No new public type and no new member on a published record. `IndexPhase` already carries `Nested`; the five
  names are internal constants and travel inside the existing `phases` shape.
- **NFR-002**: The measurement MUST NOT change what is measured: a run of this round and a run of round 17 over the same
  corpus must produce the same rows, in the same order, with the same labels.
- **NFR-003**: The clock stays one instance per build. A step measured on a second clock would land in a second report.

## Success Criteria *(mandatory)*

- **SC-001**: `index --verbose` prints five `of which …` lines under `communities`, and `--json` carries them as nested
  entries of `phases`; the block still adds up to no more than the run.
- **SC-002**: On this repository's index the five numbers are published and account for the phase: the steps that ran add
  up to within a second of `communities`.

  **Measured at landing**, 2026-11-02, this machine, this repository's index (740 files, 14 231 passages, 7 305 symbols,
  10 806 edges, 645 communities, 525.2 MiB); the run is 3 m 2 s wall / 3 m 7 s cpu and 27 commits:

  | step | wall | share of the phase | what it is |
  |---|---|---|---|
  | `nodes` | 0.1 s | — | 7 305 declarations read and ordered |
  | `adjacency` | 0.2 s | — | 10 806 relations read into neighbour lists, then renumbered |
  | `louvain` | **53.4 s** | **40 %** | the partition of 7 305 nodes, and its numbering |
  | `degree` | 0.0 s | — | the centrality metric over the same graph |
  | `assign` | **1 m 21 s** | **60 %** | 7 305 `UPDATE` statements in one batch |
  | `communities` | 2 m 15 s | 74 % of the run | the phase itself, unmoved by this round |

  Read beside the `commit` line (41.6 s, nearly all of it the writing phase's 24 checkpoints) and the `write` line
  (39.0 s), the split names two candidates and retires the rest: the whole-graph reads cost 0.3 s — 0.2 % of the phase —
  and degree centrality is below the report's resolution. The next round's target is `assign` and `louvain`, in that
  order, and not the store's read path.
- **SC-003**: `commits` counts every commit a build makes, the clusterer included: 27 on the rebuild above — 24 checkpoints
  for 740 indexed and 14 skipped files, plus the edges, the columns and `meta` — where the same arithmetic reported 26
  before this round.
- **SC-004**: The suite stays green and grows by the cases that pin the split: the five names in order and nested, a step
  that produces a value timed across its await, a corpus with no relation leaving the two algorithms at zero, and a
  build's commit count including the clusterer's batch.

## Assumptions

- **The five steps cover the pass.** The only work left outside them is filling two arrays from what the algorithms
  returned — a store-free loop over 7 305 entries — and the parent phase holds it either way.
- **A nested row does not change `other`.** The block's remainder is computed from the phases that are not nested, so
  adding five nested rows leaves it where it was.
- **`assign`'s commit is small, and saying so is part of the finding.** The step's 1 m 21 s is 7 305 statements; the commit
  at the end of them is inside the 41.6 s the `commit` line reports, and the writing phase's checkpoints account for nearly
  all of that. The next round can therefore price the statement loop without untangling a commit from it.
- **The numbers are one machine on one day.** What transfers is the ratio between the lines — two steps at 40 % and 60 % —
  not the seconds.

## Out of Scope

- **Making the clusterer faster.** That is the next round, and it now has a target the size of `assign` and `louvain`. This
  round changes nothing the pass computes.
- **Incremental clustering, or reusing the labels of unchanged files.** A tempting answer to a fixed charge and a different
  decision: it changes which labels a build produces, so it needs its own spec, its own determinism argument and its own
  tests.
- **A cheaper partition, a coarser graph, or a smaller candidate set for Louvain.** All of them are the next round's
  subject, not this one's.
- **The no-op `meta`/open overhead (5.5 s a turn).** D21's third lever, still open.
- **SC-002's 5 000-file tree and SC-006's 61×.** Untouched, as rounds 16 and 17 left them.

