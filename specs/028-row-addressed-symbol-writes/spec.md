# Feature Specification: The Write-Back Without a Parser

**Feature Branch**: `028-row-addressed-symbol-writes`

**Created**: 2026-11-03

**Status**: Accepted 2026-11-03 (constitution 1.2.0). Round 19 of `specs/000-parity-roadmap.md`. It takes the lever round
18 named first — `assign` — and answers the question round 18 asked of it: is the statement loop slow, or is the store?

**Input**: User description: "`027`'s next slice: `assign` is 1 m 21 s of one `UPDATE` per declaration at 11 ms each, and
the round left three candidates — the statement's shape, the `symbols` indexes, or the batch size."

## Why

Round 18 turned one opacity into five numbers, and the largest of them is ours:

> `communities` 2 m 15 s (74 %): `nodes` 0.1 s, `adjacency` 0.2 s, `louvain` 53.4 s, `degree` 0.0 s, `assign` 1 m 21 s.

`assign` is 7 305 executions of

```sql
UPDATE symbols SET community = @community, centrality = @centrality WHERE id = @id
```

inside one batch. At 11 ms apiece that is a second per ninety declarations, on a table the previous step read in full in
0.1 s. A store that can read 7 305 rows in a tenth of a second and not update them in a hundred and eighty is not slow; it
is paying for something a read does not pay for.

The suspicion is the parser. `IDatabase.ExecuteSQL` takes statement text plus a bag of values, renders each value back into
the text with the engine's own formatter, and parses the result — per statement, 7 305 times. This repository already
knows that road and already avoids it in one place: `IndexSql.Insert` hands rows to the table API, and its own comment says
why (the literal scanner shifted a column on an apostrophe until the engine was pinned at `2.1.2`). `IndexSql.Update` has
existed beside it since then, used for `meta` keys, and no path has ever measured what it buys.

### The measurement, before the code

A temporary probe on a controlled table — 3 001 declaration rows in one index, one batch each way, the same column values,
the same seven labels, run twice so a cold page cache could not flatter whichever went first:

| 3 001 rows, one batch | cold | warm |
|---|---|---|
| `ExecuteSQL` + parameters, as `assign` does today | 14 483 ms | 12 875 ms |
| `IndexSql.Update`, as `WriteMeta` does | 86 ms | 143 ms |

Four and a half milliseconds per statement against forty microseconds per row, on the same store, in the same batch. The
probe's absolute number is kinder than the real index's — the real `assign` pays 11 ms per statement, because 645 labels and
six text columns cost more to render and to re-index than seven labels and eight — and that is the point: the row path's
residual, 86 to 143 ms for the whole table, is index and commit work rather than parsing, and it is two orders of magnitude
below what the statements pay.

The probe is deleted, and what it measured becomes three assertions in a test that stays (the plan's D3), because the
slice's whole argument rests on this store behaving the way its interface promises.

### What round 18 left open, and what this round does not touch

Round 18's open item says `assign` is the larger lever and ours, `louvain` the smaller one and the store's. This round takes
`assign` and leaves `louvain` alone: 53.4 s over 7 305 nodes and 10 806 edges stays, and after this round it is the phase's
largest step. That is a deliberate stop rather than an oversight — the partition's cost is a decision about label
stability (a coarser graph, a bounded candidate set), and it needs its own argument.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — The write-back costs what writing costs (P1)

A developer runs `sharpcoreagent index --rebuild --verbose` and reads `assign`. Before this round it is 1 m 21 s for 7 305
rows. After it, the same rows go through the store's row API and the step drops below `louvain` — the step round 18 named
as the one nobody here owns.

**Why this priority**: it is the whole slice. One call site, one dictionary per row, and the largest cost this repository
can still remove without a decision about clustering semantics.

**Independent Test**: a build over the two-cluster corpus writes the same labels and the same centrality it wrote before,
read back from `symbols`; a rebuild of this repository's index reports `assign` below round 18's 1 m 21 s.

**Acceptance Scenarios**:

1. **Given** any index with declarations, **when** a build assigns communities, **then** every row's `community` and
   `centrality` are what the pass computed, and no other column of that row changes.
2. **Given** an index where the pass writes 7 305 rows, **when** the report is read, **then** `assign` is a fraction of the
   second-per-ninety the statement loop cost.
3. **Given** a corpus with no relations at all, **when** a build runs, **then** the rows still get an empty label and a zero
   centrality — the pass writes, the graph algorithms do not run, and neither number changes because of how the write is
   shaped.

### User Story 2 — The promise about an interrupted pass still holds (P2)

`CommunityAssigner` writes both columns in one batch so that an interrupted pass leaves the previous labels rather than half
of the new ones. That promise was made about a batch that held statements; it now has to hold for row-addressed writes,
which are a different entry point into the same batch.

**Why this priority**: second, because it is the difference between a faster write and a faster write that can corrupt a
column. It is cheap to prove and cheap to forget.

**Independent Test**: a batch that updates a row's `community` and is then cancelled leaves that row's previous label.

**Acceptance Scenarios**:

1. **Given** an open batch holding a row-addressed update, **when** the batch is committed, **then** the new value is
   stored.
2. **Given** an open batch holding a row-addressed update, **when** the batch is cancelled, **then** the previous value is
   still the stored one.
3. **Given** a row-addressed update naming two columns of a wide row, **when** it is committed, **then** the row's other
   columns keep their values.

### Edge Cases

- **A declaration with no cluster.** Its label is the empty string, and the empty string is what reaches the store — not an
  omitted column, which would leave the previous build's label in place. The read-back test covers exactly this shape.
- **A table the pass does not own.** `symbols` also carries `name`, `qualified_name`, `kind`, `doc`, `signature` and the
  line range. A row-addressed write names columns, so a bug there would be a row replace rather than an update: User Story
  2's third scenario is that bug, asserted.
- **An index with no declarations at all.** The pass returns before opening a batch, so no commit is charged and no write
  happens — unchanged from round 18 and asserted there.
- **A store that cannot find the row.** `UpdateByPrimaryKey` returns `false` for a key it does not hold. The ids come from
  the same read that produced the rows, so a miss would mean the table changed underneath a build that holds the writer
  lock, and the count of misses is not reported because that cannot happen.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `CommunityAssigner` MUST write `community` and `centrality` by primary key through the store's row API, and
  MUST NOT issue a statement per declaration.
- **FR-002**: The rows written MUST be the rows the pass read: same declarations, same labels, same centrality, in the same
  order.
- **FR-003**: The write MUST remain one batch, charged to `commit` at both ends and counted once, exactly as round 18 left
  it.
- **FR-004**: A failure inside the write MUST cancel the batch, so the columns keep their previous values.
- **FR-005**: Columns of `symbols` the pass does not compute MUST keep their stored values.
- **FR-006**: `UPDATE symbols …` MUST have no users left in this repository when the round lands: one way to write these
  two columns, not two.
- **FR-007**: Nothing the pass computes may change. No schema change, no new index, no new column, no new flag, no new
  public type, and no change to `--json`.

### Non-Functional Requirements

- **NFR-001**: `assign` on this repository's index (7 305 declarations) MUST fall from round 18's 1 m 21 s to below 5 s,
  measured on the same machine and published.
- **NFR-002**: The build's `commits` count MUST NOT move: the pass still commits once.
- **NFR-003**: The suite MUST stay green, and the new cases MUST be the ones that fail if the write path regresses to
  statements or to a row replace.

### Key Entities

- **A `symbols` row**: the stored declaration. The pass owns two of its thirteen columns (`community`, `centrality`) and
  must leave the other eleven alone.
- **The batch**: the store's unit of commit. One per passing run, one commit, both ends charged to `commit`.

## Success Criteria *(mandatory)*

- **SC-001**: A rebuild reports `assign` below 5 s, against 1 m 21 s in round 18, on the same index and machine.
- **SC-002**: A build over the two-cluster corpus stores exactly the labels and centrality round 18 stored, and every other
  column of every row is unchanged by the pass.
- **SC-003**: A cancelled batch leaves the previous labels in place, proven by a test rather than by the store's
  documentation.
- **SC-004**: `commits` is unchanged on a writing run, and a run that writes nothing still charges nothing.
- **SC-005**: No `UPDATE` statement remains in `src/`, and the suite is green with the new cases counted.

## Assumptions

- The store's `UpdateByPrimaryKey` is the write the insert path already trusts, and its batch behaviour is the batch
  behaviour of the statements it replaces. The probe asserts the first half; User Story 2's tests assert the second.
- The 11 ms per statement is dominated by rendering and parsing rather than by re-indexing, because the row path pays the
  same index maintenance and costs 40 µs. If that were false, the two numbers would be the same.

## Out of Scope

- `louvain` (53.4 s). Round 18 named it as the store's, and it needs a decision about label stability.
- The no-op `meta`/open overhead (5.5 s a turn) and the checkpoint's own size. Both are still open in `002` T034.
- Any change to what a build computes: labels, numbering, centrality, counters.
- Dropping or recreating `idx_symbols_community`. The probe says the residual write cost is small enough that the lever is
  not needed yet, and dropping the index would change the plan of every traversal query.

