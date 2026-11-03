# Feature Specification: The File the Index Left Behind

**Feature Branch**: `030-index-compaction`

**Created**: 2026-11-03

**Status**: Accepted 2026-11-03 (constitution 1.2.0). Round 21 of `specs/000-parity-roadmap.md`. It answers the one
number round 20 named and could not explain — an index file of **787.9 MiB** where round 19 measured 525.2 MiB for the
same corpus — and it answers it with an operation the product never had.

**Input**: User description: "`029`'s next slice: the index file is 787.9 MiB for a corpus whose passages and symbols
weigh tens of megabytes, and one changed file still costs 11.3 s."

## Why

Round 20 left one question open on purpose:

> the index file is **787.9 MiB** where round 19 reported 525.2 MiB for the same corpus, and `--rebuild` (twice) and
> `--prune` reproduce today's figure — the round does not explain it, does not claim it, and leaves it on `002` T034.

Two rebuilds reproducing the same figure is the clue. A file that grows with a corpus reproduces nothing; a file that
holds what earlier writes stopped using reproduces itself exactly, because a rebuild re-uses what it frees and never
gives it back. The index is one SharpCoreDB file, rows are rewritten and dropped on every writing run, and a rebuild
empties every table before filling it — so the question is not why the corpus is large, it is what the file is holding.

### The measurement, before the code

Four temporary probes, run on a copy of this repository's own index (and on a synthetic one), deleted with the round
(`specs/002-code-evidence-index/research.md` D26 keeps the numbers):

| probe | measured |
|---|---|
| the file, as it was | **787.9 MiB**; the store's own statistics: used 675.6 MiB, free 112.2 MiB, fragmentation **0.0**, 16 blocks |
| the rows, counted | 14 368 passages · 7 336 symbols · 10 879 edges · 767 files; the text of every passage 9.15 M characters, their tokens 6.17 M, headings and docs 1.7 M — **24 MB of characters** in a 787.9 MiB file |
| `VacuumAsync(Full)` on a copy | **787.9 → 73.2 MiB in 0.6 s**, reclaimed **714.6 MiB (90.7 %)**, every row still there: 14 368 · 7 336 · 10 879 · 767 |
| the same call through `IndexHandleCache`, a second handle open in the same process | 0.7 s, the same numbers — the rewrite does not need the cache closed |
| `VacuumAsync(Quick)` and `VacuumAsync(Incremental)` | **0.0 MiB reclaimed**, 0.0 s. Only `Full` compacts a single-file database |
| a one-file index, grown by hand | 6.1 MiB live → 14.1 MiB after 2 000 rows of 4 KiB → `DELETE` reclaims **nothing** (14.1 MiB) → `Full` returns **6.1 MiB in 0.02 s** with the two live passages intact |
| 2 000 single-row `UpdateByPrimaryKey` calls on that file | **57.3 s** (28.6 ms each) and **no change in size** |

Three of those rows are the finding.

**The live data is 73.2 MiB and the file is 787.9 MiB.** The index is not mostly data; it is mostly what earlier writes
stopped using, and the store can give it back in two thirds of a second with every row intact. That is 10.8× of file for
data the index already describes in `meta`.

**The store cannot tell you when to do it.** `StorageStatistics` reports `used` 86 % and `free` 14 % of a 787.9 MiB file
before the rewrite, and `used` 85 % and `free` 15 % of a 73.2 MiB file after it, with fragmentation **0.0 in both
states**. The percentages are equal because they do not measure dead space; there is no number in the statistics that
says "this file is ten times the rows it holds". A trigger based on a ratio would therefore be a guess.

**A delete frees the row and not the page.** The synthetic file is the proof at a size a test can hold: 2 000 rows of
4 KiB make a 6.1 MiB file 14.1 MiB, deleting all of them reclaims 0.0 MiB, and one `Full` vacuum at 0.02 s returns the
file to 6.1 MiB. Nothing in this product ever asked for that vacuum, which is why an index that has been rebuilt a few
times is 10.8× its data. The same ratio holds for a rebuild of this repository: it writes **165.4 MiB** for 73.5 MiB of
rows, twice in a row, and the compaction that follows returns the second number in **0.5 s** — so each rebuild spends
about 1.25× the live data on pages it then stops using, which is the arithmetic behind round 19's 525.2 MiB and round
20's 787.9 MiB and why neither figure said anything about the corpus.

The last row is not the cause and is recorded anyway: a single-row update costs 28.6 ms and does not grow the file (it
re-uses the page it freed), which is the cost round 18 measured at 11 ms per statement for the community write-back's
7 305 `UPDATE`s — and the reason the size plateau is a plateau.

### What is actually slow, and what this round does not fix

Round 20 measured a writing run at 11.3 s: `communities` 2.2 s, `commit` 7.0 s, and 5.8 s of open and no-op overhead on
a run that changes nothing. A 73.2 MiB file does not make `commit` cheap — the meta batch still costs 2.1 s after a
rewrite against 2.9 s before — and it does not touch the open, measured at 0.05 s either way. What it does change is the
disk, and every full scan of the file: the first whole-`chunks` scan of the bloated file costs 2.6 s and of the
compacted one 0.007 s. This round therefore claims the plane it can hold — 714.6 MiB of a 787.9 MiB file — and leaves
the writing-run floor exactly where round 20 left it.

## What it does

Two things, one mechanism.

1. **`index --compact`** rewrites the file and reports the arithmetic: the size before, the size after, the bytes
   reclaimed, what share of the file that is, the time it took, and the counters afterwards so a reader can see the rows
   survived. `--json` carries the same fields. It exits non-zero only when the rewrite did not happen.
2. **A finished rebuild compacts what it wrote.** A rebuild is the one command that empties every table before filling
   it, so it is the one that leaves the most behind; it now ends by giving that back and says so in one note, in the
   report that already says how long each step took.

Nothing else changes: no schema, no column, no new persisted state, no commit in the writer's count, and no behaviour on
the turn-start refresh, which keeps its three seconds and never compacts.

## Success criteria

- **SC-001** `sharpcoreagent index --rebuild` on this repository leaves an index file of at most 100 MiB, and its
  report carries one note naming what the compaction gave back. Measured: 165.4 MiB written → **73.5 MiB** in 0.5 s,
  from the 787.9 MiB the round started with.
- **SC-002** `index --compact` on an index holding dead space reclaims it and prints before, after, reclaimed and
  elapsed; a second `--compact` immediately afterwards reclaims 0 and succeeds.
- **SC-003** Every row survives the rewrite: after the rebuild of SC-001, `index --verify` reports 771 files in the
  index, 771 on disk, 0 hash mismatches and 0 orphans — 766 before this round's own five files joined the corpus.
- **SC-004** The rewrite is bounded and stated: 0.6–0.7 s for this corpus's 714.6 MiB, 0.02 s for a small one, paid once
  per rebuild rather than once per writing run.
- **SC-005** A compaction of a workspace nobody indexed creates no database file, succeeds, and says `there is no index
  to compact`.
- **SC-006** The suite passes with 0 warnings, and the new cases are counted against 1 508.

## Out of scope

- Compacting on a writing run or on the turn-start refresh: the cost scales with live data, and a turn has 3 s.
- A threshold, a schedule, or a persisted "size at the last compaction": D3 is why there is no honest input for one.
- What round 20 left: the `commit` line, the open and no-op overhead, SC-002's 5 000-file tree, SC-006's 61×. They stay
  on `002` T034.
- The store's page layout, record format and update path — including the 28.6 ms single-row update, recorded in D26 and
  not touched here.
