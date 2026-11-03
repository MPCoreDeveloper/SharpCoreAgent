# Implementation Plan: The Clusterer's Own Steps

**Feature**: `027-community-pass-steps` · **Spec**: [spec.md](spec.md) · **Tasks**: [tasks.md](tasks.md)

Round 18. One slice: the phase that is 74 % of a rebuild becomes five numbers, and the commit nobody counted starts
counting.

## Summary

One file gains a clock argument and loses a monolith. `CommunityAssigner.AssignAsync` takes the build's `IndexPhaseClock`
and records five steps on it — `nodes`, `adjacency`, `louvain`, `degree`, `assign` — while `PartitionAsync` splits into a
partition (`RankAsync`), a metric and a graph builder. `Write` charges its batch's two ends to `commit` and returns the one
commit it made; `IndexWriter` adds that to the count it reports.

Nothing the pass computes moves. No new public type, no new member on a published record, no new flag, no schema change, no
change to the rows a build writes. Five internal constants, one internal record gain, and five `of which` lines.

The split is the product, so the plan spends most of its length on the two decisions that are not mechanical: what counts as
one step (D1) and why the commit had to be separated from the write (D4).

## Decisions

### D1 — Five steps, because the pass makes five decisions

`communities` is one line in a report that covers six things: which declarations the graph addresses (`nodes`), what is
connected to what (`adjacency`), which declarations share a cluster (`louvain`), how many neighbours each has (`degree`),
and how the answer reaches the store (`assign`). A phase with six questions under one number can only be optimised by
guessing, and D21 wrote down what guessing costs on this codebase:

> Nothing timed a build's steps, so the next hour of performance work would have been aimed at a guess.

The five names are the five verbs of the pass, one word each, in the vocabulary the block already prints (`scan`, `load`,
`write`, `remove`, `edges`, `meta`, `commit`). A sixth step was considered and rejected: renumbering the declarations into
the graph the algorithms take is real work, but it is the same work as reading them and it costs 0.2 s — a row that says
`0.0 s` on every run teaches a reader to skip the block.

### D2 — Nested, not five siblings

Each of the five is time already inside `communities`, so `Nested` is `true` and the command line prints `of which …`
without a share of the run. Two consequences, both wanted:

- The block's `other` does not move, because `other` is what is left after the phases that are *not* nested. A reader who
  adds up this round's block gets the same total as last round's.
- A share of the run would be wrong five times over, and the existing rendering rule — a nested row takes its number
  without a share — already said so for `commit`.

`IndexPhases.IsNested` becomes a lookup over the nested names instead of an equality against `Commit`. The trade is that
`Order` now has thirteen entries for a routine build; the alternative was a second document or a second rendering path for
"the parts of a phase", and both are more surface than five names.

### D3 — The clock is passed in, and a step may be entered twice

`AssignAsync` takes the build's clock rather than creating one, for the same reason `IndexFileWriter.For` takes one: a build
has one report, and a step measured on a second clock lands nowhere. The clock already adds up repeated entries of one name
— `IndexPhaseClockTests` has pinned that since D21 — so `nodes` is measured twice (read, then the position map) and
`adjacency` twice (read, then the renumbering) instead of inventing four names for two halves of one decision.

The clock gains a `Func<Task<T>>` overload. The store's `LouvainAlgorithm.ExecuteAsync` and `DegreeCentrality.CalculateAsync`
return `Task<T>`; the build's own awaited steps return `ValueTask<T>`. Without the overload the two algorithms would have to
be wrapped, and a measurement that changes the shape of the code it measures is a measurement with a thumb on the scale.
Checked against the assembled types rather than assumed, because the package ships no source.

### D4 — The clusterer's commit is separated from the clusterer's write

`assign` writes 7 305 rows one statement at a time inside one batch. The commit at the end of that batch was invisible twice
over: `CommunityAssigner` does not go through `IndexFileWriter`, so neither the `commit` phase nor the `Commits` counter saw
it. A build that made 27 commits reported 26, and `assign`'s number would have included a store charge a reader would
reasonably attribute to the statements.

Both ends of the batch are charged to `IndexPhases.Commit`, and the commit is returned by `Write` and added to the count in
`IndexWriter`, exactly as `RunInBatch` does for the rows it writes. Two sites, one rule, no third: they are the two places in
this codebase that call `EndBatchUpdate`, and that is written down where the count is.

The three checkpoint assertions in `CheckpointTests` move from ⌈files ÷ 32⌉ + 2 to + 3. That edits last round's tests, and
it is the point of this decision: the number they pin was one short, and leaving it short to avoid touching a green test
would make this round's numbers disagree with its own report.

### D5 — Numbering belongs to the partition, so `RankAsync` returns ranks

A community's label is not what Louvain returned; it is the rank of the community's ordinally-first member, the rule that
makes two builds of one corpus agree. Numbering after the metric would have put that work outside every step and left a gap
between the five rows and the phase they explain.

So the numbering moves into the step that produces the partition: `RankAsync` runs Louvain, maps the members back to
positions and orders the communities, returning one array of positions per rank. `PartitionAsync` keeps only the two loops
that fill `labels` and `centrality` from those results — a store-free pass over 7 305 entries, microseconds, held by the
parent phase. The behaviour is identical; the previous order (Louvain, metric, numbering) is not preserved because the
metric does not depend on the labels.

### D6 — Both clocks, because the phase's neighbours are lopsided

Every step reports wall clock and processor time, like every phase before it. The pair is what makes this round's numbers
readable: `louvain` and `assign` report 53.4 s and 1 m 21 s of wall clock and almost the same processor time, so neither is
waiting on the store's IO — both are working. A step that reported a minute of wall clock and two seconds of processor time
would have been a different finding, and this report can now make that distinction.

### D7 — The measurement is published with the round, not kept for the next one

The round's product is a number, so the number goes in the spec, the plan and `002`'s `research.md` beside the code that
produced it. A split that lands without its measurement is a refactor with a story attached.

## What the change touches

| File | Change |
|---|---|
| `src/SharpCoreAgent.Indexing/IndexPhases.cs` | The five names, their order after `communities`, and `IsNested` as a lookup over six nested names. |
| `src/SharpCoreAgent.Indexing/IndexPhaseClock.cs` | A `Func<Task<T>>` overload beside the `Func<ValueTask<T>>` one; the generic overload now returns the value instead of dropping it. |
| `src/SharpCoreAgent.Indexing/Graph/CommunityAssigner.cs` | The clock parameter, five `Measure` sites, `PartitionAsync` split into `RankAsync`, `ReadGraph`, `BuildGraph` and `PositionsOf`, and `Write` charging and counting its commit. |
| `src/SharpCoreAgent.Indexing/IndexWriter.cs` | Passes the clock, keeps the assignment, adds its commit to the count. |
| `src/SharpCoreAgent.Domain/Index/IndexPhase.cs` | Documentation: which names exist, and that six steps are nested. No member changes. |
| `src/SharpCoreAgent.Domain/Index/IndexBuildReport.cs` | Documentation of `Commits`: the community columns are one of the commits it counts. |
| `tests/…/Indexing/IndexPhaseClockTests.cs` | The nested set, the order of the clusterer's names, and a value-producing step timed across its await. |
| `tests/…/Indexing/PhaseTimingTests.cs` | The commit test sums the `commit` step instead of every nested one; a new test pins the five steps on a corpus with no relation. |
| `tests/…/Indexing/CommunityAssignerTests.cs` | A corpus with two clusters reports all five steps as nonzero and nested. |
| `tests/…/Indexing/CheckpointTests.cs` | Three expected counts, + 1 for the clusterer's commit. |
| `tests/…/Cli/IndexCommandTests.cs` | Thirteen phases in `--json`, the clusterer's five in order, and one `of which nodes` line in `--verbose`. |


## The measurement

One rebuild of this repository, `index --rebuild --verbose`, logged outside the repository so the run cannot index its own
log (round 17's first attempt did, and finished `stale`):

| phase | wall | cpu | share |
|---|---|---|---|
| `scan` | 0.1 s | 0.1 s | 0 % |
| `load` | 0.0 s | 0.0 s | 0 % |
| `write` | 39.0 s | 43.2 s | 21 % |
| `remove` | 0.0 s | 0.0 s | 0 % |
| `edges` | 2.0 s | 2.1 s | 1 % |
| `communities` | **2 m 15 s** | 2 m 15 s | **74 %** |
| of which `nodes` | 0.1 s | 0.1 s | |
| of which `adjacency` | 0.2 s | 0.2 s | |
| of which `louvain` | **53.4 s** | 53.2 s | |
| of which `degree` | 0.0 s | 0.0 s | |
| of which `assign` | **1 m 21 s** | 1 m 21 s | |
| `meta` | 1.9 s | 1.9 s | 1 % |
| of which `commit` | 41.6 s | 44.9 s | |
| `other` | 4.1 s | 4.6 s | 2 % |
| `commits` | 27 × the store's batch commit | | |

The run: 740 files, 14 231 passages, 7 305 symbols, 10 806 edges, 645 communities, 525.2 MiB, 3 m 2 s wall / 3 m 7 s cpu.

What it says:

- **The reads were never the problem.** `nodes` and `adjacency` are 0.3 s together — 0.2 % of the phase. Reading 7 305 and
  then 10 806 rows from this store costs a fifth of a second, which retires a suspicion the write phase's history
  encouraged.
- **`assign` is the largest part: 1 m 21 s for 7 305 statements.** That is 11 ms per `UPDATE` inside one batch, an order of
  magnitude more per statement than the ~1.7 ms a file's mixed inserts cost in the write phase. Whether it is the
  statement's shape, the `symbols` indexes, or the batch's size is the next round's first question.
- **`louvain` is 53.4 s on a 7 305-node, 10 806-edge graph.** A partition of that size is a second's work in most
  implementations, so this one is two orders of magnitude slower than the shape alone explains, which makes it a candidate
  rather than a fact about the corpus.
- **`degree` is below the report's resolution** while running over the same graph, which says the 53.4 s above belongs to
  Louvain rather than to the graph's size.

## Verification

- `dotnet build SharpCoreAgent.slnx -c Release`: 0 warnings, 0 errors.
- `dotnet test SharpCoreAgent.slnx -c Release`: 1 498 passed, 0 failed, 0 skipped (round 17 landed 1 494).
- `index --rebuild --verbose` on this repository: the block above, `state fresh`, `--verify` healthy.
- `index --json`: thirteen phases, the clusterer's five nested and in order, and still no `commits` key.

## Complexity Tracking

| Complexity | Why it is worth it |
|---|---|
| Five more rows in every `--verbose` block, and a report of 13 entries instead of 8 | The alternative is a phase nobody can price. The rows are behind a flag, they are nested (so the block's arithmetic is unchanged), and they are the difference between "communities is 74 %" and a target. |
| `nodes` and `adjacency` are each measured twice | Each pair is one decision split for the reader's sake, not two decisions; the clock was built to add repeated entries up, and its test already says so. |
| A fifth `MeasureAsync` overload on the clock | The store returns `Task<T>` and the build returns `ValueTask<T>`; wrapping either into the other to be timed would be paying for the measurement. |
| A commit is now counted in two places | Those are the two places that commit. The rule ("count it where you time it") is unchanged; the number of sites it applies to grew from one to two, and the round is what discovered the second. |

