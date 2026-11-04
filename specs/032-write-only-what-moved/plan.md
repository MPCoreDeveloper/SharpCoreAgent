# Implementation Plan: The Rows That Already Said It

**Feature**: `032-write-only-what-moved` · **Spec**: [spec.md](spec.md) · **Tasks**: [tasks.md](tasks.md)

Round 23. One slice: the community pass stops rewriting rows that already say what it computed. Two files in `src`, one
test file, no schema, no flag, no public type — and one commit fewer on every writing run whose partition has not moved.

## Summary

`CommunityAssigner.SelectSymbols` selects `community` and `centrality` beside `id` and `key`, so `SymbolNode` carries the
answer the row holds. `Write` walks the declarations once, keeps the rows whose stored label or stored degree is not the
one it computed, and opens the store's batch only when that list is non-empty — so a pass over an unchanged partition
returns `Commits` 0 without a batch, a commit or a row. `IndexSql.AsDouble` reads a `REAL` column the way `AsLong` reads
an `INTEGER` one, and `SameDegree` compares two degrees by their bits, which is both what the comparison means and the
only form the analyzer's floating-point-equality rule accepts.

Nothing else changes: no schema, no column, no flag, no exit code, no `--json` field, no new public type, and no
behaviour on a pass whose answers did move.

## Decisions

### D1 — The pass wrote every row because it never read one

`SELECT id, key FROM symbols` is all the pass ever asked for, so the two columns it owns were write-only from its point
of view: the only rows it could skip were the ones it could not see. The fix is one clause in the projection and two
fields on the record the projection becomes — not a "did the graph change" flag on `IndexWriter`, which would have been a
second copy of a condition computed from a report the pass does not own, and which could not see a single stale row.

### D2 — The comparison is made in the form a reader sees

Two shapes had to be reconciled, and both were found by running it rather than by reading it:

- **A label that does not exist.** `AssignAsync` allocates `labels` and fills it only for connected declarations; an
  isolated one keeps `null`. `Write` hands that `null` to `IndexSql.Update`, whose row bag omits the column, so the store
  holds "no value" and reads back `DBNull`. Comparing `null` with `""` says "different" for every isolated declaration,
  which is why the first version rewrote exactly the rows it was supposed to skip.
- **A degree that is a whole number.** `1.0` reads back as `Int32 1` and an isolated row's `0` as `DBNull`. `AsDouble`
  normalises both, and `AsDouble(DBNull)` is `0`, which is the value the pass computed.

So the pass compares `symbols[index].Community` (empty text for no label) with `labels[index] ?? string.Empty`, and both
degrees through `AsDouble` and `SameDegree`. It still *writes* the raw label, `null` and all, so the stored
representation is exactly what it was before this round — the change is what gets written, never how.


### D3 — The same number, not a close one

`SameDegree` compares `BitConverter.DoubleToInt64Bits` of the two values. A tolerance would answer "unchanged" for a
degree that moved by less than the tolerance and leave the old value in the row — the one outcome the comparison exists
to prevent — and centrality is a ratio of small integers over a graph of thousands of nodes, so a real move is far larger
than any epsilon worth choosing. The two values compared here are also the same arithmetic run twice, not two
measurements of a quantity, so there is no noise to absorb.

The bit form is also what the build accepts. The first version was `Centrality.Equals(centrality)`, which is correct and
which `S1244` reports as *"do not check floating point equality with exact values"*; this repository's gate is 0
warnings, so the comparison had to be written in a form that says what it means. A pair that is equal but has different
bits, such as `0` and `-0`, costs a row: never an answer.

### D4 — The lever is whether to commit, not how much to write

The two probes that decided the round's scope are in the spec's table: `write` is 7.2 s for a 103-line document and
7.6 s for a 700-line source file, and 7.6 s again at 110.4 MiB, while `meta` is 1.9–2.1 s for ten rows. Commit cost
follows the tables a batch touches, not the rows it carries, so no amount of writing less inside the write batch would
have moved it. Whether the community batch exists at all is a different question, and it is the one this round answers.

That also fixes the shape of the next slice: the remaining commits are the write batch and `meta`, both of which have
something to record on a writing run, so the next lever is making the run's commits fewer rather than smaller — a
failure-unit argument (`026`) rather than a performance one.

### D5 — What the tests had to be argued into

Both tests moved twice before they proved anything, and both moves are worth keeping in the record:

- **The stale label cannot live in the file that changes.** Re-extracting a source file deletes and re-inserts its symbol
  rows with the extractor's defaults, so a stale label on a declaration whose own file changed is wiped by the write
  before the pass ever looks. The test provokes the stale row on an isolated declaration and then changes a *document*,
  which leaves that row alone.
- **The stale row cannot be written through a second handle.** A lease that has been released does not leave the row it
  updated where the next handle can read it: a write on one lease and a build on the next lost the write, and the test
  passed for the wrong reason — three commits, no community row, and a label that had never been `c99` in the first
  place. The test now holds one lease across both builds and asserts the label it wrote is readable before it builds.

### D6 — What stays open

The `meta` batch and the write batch, 2.1 s and 6.7 s of a 9.2 s run, both of which carry a row that genuinely changed;
the run's commit count, which is now 2 and could be 1 at the cost of the failure unit `026` bought; the store growing
33 % across four writing runs of one changed file; and, unchanged from round 22, `--prune` that removes a file (10.1 s),
SC-002's 5 000-file tree, SC-006's size ratio and SC-003's p95. `002` T034 owns all of them.

## Files

| file | change |
|---|---|
| `src/SharpCoreAgent.Indexing/Graph/CommunityAssigner.cs` | `SelectSymbols` reads both owned columns; `SymbolNode` carries them; `Write` builds `moves` and returns 0 without a batch when it is empty; `SameDegree`; the docs that said "every row is rewritten" say what is true now |
| `src/SharpCoreAgent.Indexing/IndexSql.cs` | `AsDouble`, beside `AsLong` and `AsText` |
| `tests/SharpCoreAgent.Tests/Indexing/CommunityAssignerTests.cs` | `APassThatFindsTheSameAnswersWritesNothing`, `AStaleLabelIsTakenBackEvenWhenTheGraphHasNotMoved`, `BuildReport`/`WriteAsync`/`WriteCommunity` helpers, `ReadSymbols(IDatabase)` |
| `specs/032-write-only-what-moved/{spec,plan,tasks}.md` | this round |
| `specs/000-parity-roadmap.md` | row 23 in the rounds table, the round section, round 22's commit recorded |
| `specs/002-code-evidence-index/tasks.md` | the note under T034 |

## Complexity Tracking

One `internal` helper on `IndexSql`, two fields on a private record struct, one private predicate and one list per pass.
No type, no column, no persisted key, no configuration, no dependency, and nothing on the public surface — `Commits` on a
writing run is one lower because one fewer commit is made, which is a fact rather than an addition.

The one thing worth naming as a cost is that the pass now depends on how the store represents "no value": the comparison
lives beside the read, and both are pinned by a test that stores a label by hand and expects it taken back. That test is
also the reason the failure mode is bounded — a representation the comparison reads wrong makes the pass write a row it
did not have to, never leaves a wrong value in one.
