# Implementation Plan: One Commit Per Checkpoint

**Feature**: `026-checkpointed-index-writes` · **Spec**: [spec.md](spec.md) · **Tasks**: [tasks.md](tasks.md)

Round 17. One slice: a build reaches the store once per bounded group of files instead of once per file.

## Summary

Two files and one rule. `IndexFileWriter` grows a **checkpoint** — a lazily opened engine batch with an explicit
`BeginCheckpoint`, `EndCheckpoint` and `AbandonCheckpoint`, a commit counter, and a `RunInBatch` that joins an open
checkpoint instead of opening its own batch. `IndexWriter` closes a checkpoint every 32 files, and on the two paths
that stop a build early (budget, cancellation), before it stops. The remove phase and `PruneAsync` remove their whole
set in one commit each.

Nothing else moves: no flag, no schema, no reader, no query, no published JSON. One internal record gains a count, and
`index --verbose` prints it, because the round's claim is arithmetic and arithmetic deserves a number a command prints
rather than a number a probe remembers.

## Decisions

### D1 — A checkpoint, not one batch for the whole build

One batch per build would be 700 → 1 and roughly thirty times faster than this round's 700 → 26, and it would spend the
property D21 said to keep in the same sentence it named the fix:

> The per-file batch was chosen because it is the largest unit that can fail independently … and that property is worth
> keeping, so the fix is not one batch for the whole build but a checkpoint interval.

A whole-build batch also makes a build uninterruptible in a way this product cannot afford: the turn-start refresh runs
inside a budget, a `--rebuild` runs inside an hour, and a machine that is suspended mid-run must lose a bounded amount
of work. Thirty-two files is that bound; the rest of the argument is D3.

### D2 — The batch opens lazily, so a checkpoint that writes nothing costs nothing

`BeginCheckpoint` marks intent and touches no database. The engine's `BeginBatchUpdate` runs at the first write inside
the checkpoint, and `EndCheckpoint` commits only if one was opened. That keeps three existing promises exactly:
a no-op run still commits once (for `meta`) and still costs the 5.5 s D21 measured; a build stopped before its first
file commits once; and a prune of zero files commits nothing at all.

The alternative — begin at `BeginCheckpoint` and commit unconditionally — would add an empty commit to every run that
found nothing to do, which is the one run this repository works hardest to keep free.

### D3 — Thirty-two files, a constant, and no flag

The arithmetic is D21's: 1.36 s for a commit against about 1.5 s for a file on this index. At 32 files a checkpoint
removes 97 % of the commits, and a crash costs at most about fifty seconds of re-work. A smaller checkpoint buys little
(a checkpoint of 4 still pays 175 commits); a much larger one makes a crash expensive on a corpus of large files, where
the per-file cost is seconds rather than one and a half.

A count rather than a duration, and a constant rather than a flag:

- **A duration would be untestable without a fake clock and non-deterministic in the product.** "Commit at most every
  N seconds" bounds the seconds a crash wastes, which is the number a developer feels — but this build already carries
  two clocks because a suspended machine's wall clock lies (D19), and a loop that committed on wall time would behave
  differently on a suspended machine than on a busy one for no reason a caller asked for.
- **A flag would move the decision to the one place that cannot make it.** Every caller of a build — the refresh, the
  rebuild, the picker's task, `--index-budget`'s callers — would have a way to choose badly, and none of them knows the
  index's size or the store's price per commit.

The constant is `IndexWriter.CheckpointFiles`, and the writer takes the number as an optional parameter so tests can
prove the arithmetic with two files instead of thirty-two.

**And the measurement revised the constant's reasoning while leaving it alone.** With the change in, the per-file write
is 49 ms (36.9 s across 754 files) and one commit is 1.44 s, so at 32 files a checkpoint the commit is still about 48 %
of the write phase — a group of 128 would take another nine seconds off this rebuild. What stops the round there is that
a checkpoint is also a buffer: 128 files' rows and vectors are held in memory until the commit, and how much memory is
the question D21 did not ask and this round does not answer. The interval is therefore recorded as an open item with its
arithmetic attached rather than tuned to one machine's corpus.

### D4 — Abandoning a checkpoint is the store's own undo, not ours

`CancelBatchUpdate` discards the rows the batch buffered, so an abandoned checkpoint leaves the store exactly as the
last commit left it, with no compensation code and no delete statements to get wrong. The build then fails the way it
fails today — the exception leaves the writer, the report is never built, the maintainer's `finally` releases the lock
— and `meta` was never written, so the index reads **stale** rather than wrong.

D21's remarks already record the pairing rule the engine imposes (`CancelBatchUpdate` is not called after a committed
`EndBatchUpdate`). This round adds the second half of that rule: a cancel is only ever the *checkpoint's*, and the
checkpoint is closed by exactly one of `EndCheckpoint` or `AbandonCheckpoint`.

### D5 — The budget and the cancellation commit before they stop

Today every file is committed as it is written, so a run that stops on its budget keeps everything it wrote and its
counters describe rows that exist. Moving the boundary without touching those two paths would break the property
silently: a run that stopped with 31 uncommitted files would report `added 31` and hold none of them, and the next run
would re-derive them — correct, but the numbers in between would be a lie.

So the loop commits the open checkpoint before it breaks for budget, and before it lets
`ThrowIfCancellationRequested` out of the writer. Everything else — any other exception — goes to `AbandonCheckpoint`
and the build fails, which is D4.

`RefreshBudgetTests` already pins the shape of both paths ("stops early, leaves a usable index", "a processor-time
budget still finishes the workspace"); this round adds the case where the stop lands *inside* a checkpoint, by giving
the writer a checkpoint larger than the corpus and a budget of zero.

### D6 — The remove path gets the same rule, because it is the same rule

`RemoveVanished` deletes one row set per vanished file and `PruneAsync` does the same thing for a whole prune, each
delete its own batch and its own commit. On this index that is 1.36 s per deleted file: pruning three hundred vanished
files would spend seven minutes of commits deleting three hundred rows.

Both get one checkpoint for the whole set. It is the same lever, in the same file, with the same failure unit — a prune
that dies leaves the store as the last commit left it, and running it again is a no-op for what it already removed
because those rows are simply no longer there.

### D7 — `Commits` is counted where the commit is timed, and printed behind `--verbose`

The round's claim is arithmetic — 700 commits become 24 — and the only honest way to check it is to count them, not to
watch the clock and infer. `IndexFileWriter` increments a counter at the one place it calls the engine's
`EndBatchUpdate`, so the count and the `commit` phase cannot disagree, and `IndexWriteResult` carries it out.

It reaches the terminal behind `--verbose`, next to the phase block it explains, and it does **not** reach `--json`.
The distinction is deliberate: the JSON document is what other programs read about the *index* and this number is a
fact about the *writer's strategy* on the machine that ran it, which is exactly the kind of thing D18 and D21 put
behind a flag instead of in a contract.

### D8 — A checkpoint is a commit boundary, not an accounting one

`_added`, `_changed`, `_removed`, `_unchanged`, `_skipped` and `_written` stay per file, incremented where they are
incremented today. The checkpoint carries rows, not counters — which is why nothing about the report changes when a
checkpoint grows or shrinks, and why a test can pin the counters of a two-file build and a thirty-two-file build to the
same numbers.

### D9 — Rejected: `InsertBatch`, retryable checkpoints, and a narrower loop

- **`InsertBatch`.** T034's original sentence ("then batch the row writes") is retired by D21's measurement: each
  file's rows are already one batch, and the cost is `EndBatchUpdate`, not the code between the batch's two ends. There
  is nothing to batch harder inside a commit that costs the same as thirty-two of them.
- **Retrying an abandoned checkpoint.** A file that throws kills the build today, and a retry would need a per-file
  undo the engine does not have. The bounded loss is the answer instead of a clever recovery.
- **Shrinking the per-file work (extraction, embedding).** `write` minus `commit` is 12 % of a rebuild. Optimising that
  before the 88 % would be the mistake D21 was written to prevent.
- **A checkpoint per shard, in parallel.** The loop is single-threaded and one batch is open at a time (FR-007); the
  engine's batch is not a thing to share across threads on the strength of a plan.

## What the change touches

| File | Change |
|---|---|
| `src/SharpCoreAgent.Indexing/IndexFileWriter.cs` | The checkpoint itself: `BeginCheckpoint`, `EndCheckpoint`, `AbandonCheckpoint`, `FilesInCheckpoint`, `Commits`; `RunInBatch` joins an open checkpoint instead of opening its own batch, and counts the commit where it times it. |
| `src/SharpCoreAgent.Indexing/IndexWriter.cs` | `CheckpointFiles = 32` (a constant, and an optional parameter for tests), the loop's four boundaries, one checkpoint for the vanished set, and the count out through `IndexWriteResult`. |
| `src/SharpCoreAgent.Domain/Index/IndexBuildReport.cs` | `Commits`, defaulting to `0` so a prune and a blocked report say "no build, no commits" without a second constructor. |
| `src/SharpCoreAgent.Indexing/SharpCoreDbIndexMaintainer.cs` | `PruneAsync`'s whole set in one checkpoint; the count reaches the report. |
| `src/SharpCoreAgent.Cli/Commands/IndexCommand.cs` | One `commits` line inside the `--verbose` block. `--json` is untouched (D7). |
| `tests/SharpCoreAgent.Tests/Indexing/CheckpointTests.cs` | New: the arithmetic, the abandoned checkpoint, the budget and cancellation paths. |
| `tests/SharpCoreAgent.Tests/Cli/IndexCommandTests.cs` | The `commits` line, and its absence without `--verbose`. |

## The measurement

One `index --rebuild --verbose` on this repository's own index, this machine, 2026-11-02. The baseline is `002` D21's
rebuild of the same index (698 files, 350.2 MiB), measured on the same machine the day before; the index has grown since
— 740 files, 525.2 MiB — and a commit costs more the bigger the index is, so the ratio below is if anything flattering
to the old number.

```
indexing D:/repos/MPCoreDeveloper/SharpAgent (lean, hashed-local/512)
  scanned     740 files · 740 added · 0 changed · 0 removed · 0 unchanged · 14 skipped
  content     14 200 passages · 7 291 symbols · 10 778 edges
  communities 642 · centers 642
  wrote       .sharpcoreagent/index in 2 m 53 s wall · 2 m 57 s cpu (525.2 MiB)
  phases      scan               0.1 s wall ·   0.1 s cpu     0%
              load               0.0 s wall ·   0.0 s cpu     0%
              write             36.9 s wall ·  40.8 s cpu    21%
              remove             0.0 s wall ·   0.0 s cpu     0%
              edges              1.8 s wall ·   1.8 s cpu     1%
              communities      2 m 8 s wall · 2 m 8 s cpu    74%
              meta               1.9 s wall ·   1.9 s cpu     1%
              of which commit   37.5 s wall ·  40.5 s cpu
              other              4.1 s wall ·   4.6 s cpu     2%
              commits          26 × the store's batch commit
  state       fresh
```

| Phase | D21 (before) | this round (after) | |
|---|---|---|---|
| the run | 17 m 51 s | **2 m 53 s** | 6.2× |
| `write` | 15 m 47 s (88 %) | **36.9 s** (21 %) | 25.7× |
| of which `commit` | 15 m 49 s | **37.5 s** | 25.3× |
| `communities` | 1 m 56 s (11 %) | 2 m 8 s (74 %) | untouched, and now the run |
| `edges` | 1.8 s | 1.8 s | untouched |
| `meta` | 1.9 s | 1.9 s | untouched |
| commits | **700** | **26** | 27× |

**What that says.** The write phase and the commit inside it agree to within a second, before and after: what the commit
costs is what the write costs, and the round moved the two together. 26 is the arithmetic the spec asked for —
⌈754 ÷ 32⌉ = 24 checkpoints for the 740 indexed and 14 skipped files, plus one for the edges and one for `meta`. No
vanished file was removed by this rebuild, so the removal's commit did not run; a run that drops files pays one more.

**What it does not say.** `communities` is now 74 % of a rebuild and it did not move, exactly as D21 predicted for a
*single writing run*: it clusters the whole graph whenever anything changed, so a rebuild pays 2 m 8 s whatever it
writes. That is the next lever, not this one's failure. And the ratio is one machine on one day, again: the useful
number is 700 → 26, which is arithmetic, rather than the seconds, which are this machine's.

**A writing run, for the other half of the story.** The same command with seven files changed — this round's own edits —
takes 2 m 18 s: `write` 6.2 s and the commit inside it 8.1 s, then `communities` 2 m 2 s, which is 88 % of it. D21's
26-file run spent 2 m 5 s in write-and-commit and 2 m 59 s in total; this one spends 14 s and 2 m 18 s. The fixed charge
D18 could see from the outside was two charges, and the one that is left is the clusterer.

**And the read side is where round 16 left it.** `mcp bench` after this round printed `p95 260 ms · cold 2774 ms ·
parity 12/12` — SC-005's budget, still met, with the snapshot cache and the lease untouched.

**The size is not the point here either.** 525.2 MiB against 350.2 MiB is the corpus growing (three rounds of specs) —
SC-006's 61× is D17–D19's subject and this round neither improved nor worsened it.


## Verification

- `dotnet build SharpCoreAgent.slnx -c Release` — zero warnings, zero errors.
- `dotnet test SharpCoreAgent.slnx -c Release` — the suite green, with the new tests counted.
- `IndexPhaseClockTests` and `PhaseTimingTests` stay unedited: the commit is still nested inside the phases that
  contain it, and a build's phases still add up to no more than the run took.
- `RefreshBudgetTests`, `VerifyPruneTests`, `WriterLockTests` and `IncrementalIndexTests` stay unedited: what a build
  writes, what a budget stops and what a prune removes are the properties that must not move.

## Complexity Tracking

No violation of the constitution. The new state is two booleans, one counter and one ceiling on a type that already
owned the batch boundary; the alternative — a transaction object threaded through `Write`, `WritePlaceholder`,
`Delete`, `WriteEdges` and `WriteMeta` — would be more surface for the same guarantee, and would put the decision of
when to commit at five call sites instead of one loop.


