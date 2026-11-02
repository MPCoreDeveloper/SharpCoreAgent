# Feature Specification: One Snapshot, Served

**Feature Branch**: `025-served-index-snapshot`

**Created**: 2026-11-02

**Status**: Accepted 2026-11-02 (constitution 1.2.0). Round 16 of `specs/000-parity-roadmap.md`. It closes the
open item `013` recorded against SC-005 on 2026-11-01; it does not touch that round's other criteria.

**Input**: User description: "SC-005: share one index snapshot across `mcp serve`'s per-call handles, and learn
what the 0.36 s open leaves on the table."

## Why

Round 6 published this repository's index to other agents over stdio and measured what a call costs. It cost
3 199 ms at p95 against a criterion of 300 ms, and the round said so instead of moving the goalposts: the budget
was recorded as missed, with the mechanism named in `013/plan.md:271-281` —

> `LeasedCodeIndex` builds a new `SharpCoreDbCodeIndex` per operation, the `build_id`-keyed cache in `IndexReader`
> lives inside that instance, and every served call therefore reloads all 12 889 chunk rows with their text and
> their 512-wide embeddings before it can rank anything.

That paragraph ended with work rather than a conclusion: *share one snapshot across per-call handles, and say what
`warm` means in the harness*. This round is that work.

The lease itself is not the bug. `LeasedCodeIndex` exists because a server an editor keeps open for eight hours
must not hold the database file: the handle a query leaves behind is what makes `sharpcoreagent index` in a
terminal wait. So the handle must stay per call, and what this round moves out of the call is the **snapshot** —
the in-memory view of one build that `IndexReader` loads and that the five retrieval legs read.

### The measurement this round starts from

Taken 2026-11-02 on this repository's own index — 712 files, 13 686 passages, 350.2 MiB — by one probe that timed
each phase of the read path in one process (`plan.md` carries the probe and its output):

| Phase | Cost |
|---|---|
| Opening the database, first / subsequent | 55 ms / **1 ms** |
| Reading the one `meta['build_id']` row | 42 ms |
| Loading the snapshot, cold / already loaded | **3 917 ms** / 0 ms |
| A call as the lease builds it today (fresh instance, fresh reader) | 2 433 / 2 225 / 2 439 ms |
| A call on an instance that has already loaded | 2 327 / **43 / 36 ms** |
| `find_symbol` and `index_status` on a loaded instance | 13 ms / 45 ms |
| Computing the status on a fresh instance / already computed | **1 347–1 435 ms** / 0 ms |

Two things were guesses before the probe and are facts after it. The open is **not** the cost — round 6's 0.36 s
figure was a whole process, and the per-call reopen is one millisecond. And the snapshot load is not *some* of the
cost: it is all of it, 3.9 s, paid again on every call because the reader that cached it was disposed with the
handle. A call that keeps the snapshot and drops the handle lands at **36–45 ms**, inside the 300 ms round 6 wrote
and left unmet.

### The second measurement, after the first half was built

The snapshot half was measured again through the harness rather than a probe, and the harness said 1 508 ms — not
40. A second probe found the rest of it in one line: a **status** computed on a fresh instance costs 1 347–1 435 ms,
while a search on the same fresh instance costs 34–38 ms. Every one of the five tools reads a status before it
answers, because the heading carries the freshness and a missing index has to be refused; `SharpCoreDbCodeIndex`
cached that status against the file's stamp, in a field the per-call instance took with it.

So the round's subject is one thing stated twice: **what a call must not throw away is everything it learned about
a build it did not change.** The snapshot is the expensive instance of that; the status is the other one, and it is
1.3 s of a 40 ms answer. Both move to the same per-database state, with the keys they already had — `build_id` for
the build, the file's stamp for the status.

## What already exists, and is not being rebuilt

| Piece | Where | Used as |
|---|---|---|
| Per-call handle and release | `LeasedCodeIndex`, `IndexHandleCache` | unchanged — the lease's promise is this round's premise |
| Snapshot and its cache key | `IndexReader.Load`, keyed on `meta['build_id']` | unchanged in meaning, now shared across instances |
| The five retrieval legs | `LexicalRetriever`, `VectorRetriever`, `SymbolRetriever`, `ReferenceRetriever`, `NeighbourhoodRetriever` | unchanged; they already read only the snapshot |
| Status path | `SharpCoreDbCodeIndex.GetStatus`, cached against the file's length and write time | unchanged |
| The harness | `sharpcoreagent mcp bench`, twelve fixture queries | gains the warm-up that makes its p95 mean something |
| The criterion | `013/spec.md` SC-005, already restated twice | restated once more, with the pair of numbers this round measured |

Nothing in indexing, extraction, writing or ranking changes. The database is not touched, the schema is not
touched, and no build gets faster.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The tenth question an editor asks is answered in milliseconds (Priority: P1)

A developer has Claude Code open beside a terminal. The agent has been talking to `sharpcoreagent mcp serve` all
morning; it has asked nine questions and the index has not changed since the 05:24 build. The tenth is answered
from the snapshot the first one loaded. What the call costs is the read of one `meta` row plus the ranking, not
another walk of 13 686 passages.

**Why this priority**: it is the whole round. Round 6 could publish the index but not afford to serve it; this is
the affordance.

**Independent Test**: `sharpcoreagent mcp bench` on a built index reports a warm p95 inside 300 ms, with the cold
first call printed beside it, and parity 12/12 across the fixture.

**Acceptance Scenarios**:

1. **Given** a built index and a running `mcp serve`, **When** a client asks a second question, **Then** the
   answer is computed from the snapshot the first call loaded, and the call costs tens of milliseconds.
2. **Given** the same server, **When** the developer rebuilds the index in a terminal, **Then** the next served
   call answers from the new build — the snapshot is re-keyed by `build_id`, not held by a promise.
3. **Given** an idle server, **When** a terminal runs `sharpcoreagent index`, **Then** the build is not waiting on
   the server's handle: the handle is still opened per call and released in a `finally`.

### User Story 2 - The number the harness prints is the number a client feels (Priority: P2)

A maintainer reads a `mcp bench` table and has to know which call the p95 describes. With twelve samples the
nearest-rank p95 *is* the slowest sample, so before this round the p95 was always the first call — the one that
paid the load — and a warm server measured as a cold one. The harness now spends one untimed call to warm the
snapshot and reports that call separately, so both numbers are visible and neither impersonates the other.

**Why this priority**: a harness that cannot tell warm from cold would report this round as a failure on the day
it lands, and every later measurement would inherit the confusion.

**Independent Test**: a `BenchReport` with a cold sample and twelve warm rows prints both; the p95 ignores the
cold sample; a two-query run makes three stdio calls.

**Acceptance Scenarios**:

1. **Given** the fixture's twelve questions, **When** the bench runs, **Then** the summary line carries both
   `p95` (warm samples) and `cold` (the untimed warm-up), and the rows carry the twelve warm calls.
2. **Given** a run whose first call is slow and whose rest are fast, **When** the report is rendered, **Then** the
   slow number appears as `cold` and does not become the p95.

## Functional Requirements

- **FR-001**: One loaded build **and one computed status** MUST be shared per index database, per process. Every
  `SharpCoreDbCodeIndex` opened on the same database uses the same state — the same reader and the same status memo —
  whether it was built by `LeasedCodeIndex` for one call, by the maintainer for a refresh, or by a host that keeps
  one instance.
- **FR-002**: Freshness MUST keep its present meaning, and MUST keep the key it already had: `meta['build_id']` for
  the build, the database file's length and write time for the status, both read by the call itself. A build in this
  process or another changes them, so the next call reloads and recomputes. No timer, no file watching, and no
  shortcut that answers a *build* question from a stamp.
- **FR-003**: The handle MUST stay per call. `LeasedCodeIndex` continues to open one handle per operation and
  release it on every path, including failure and cancellation: what is shared is memory, not a lock.
- **FR-004**: The shared state MUST be bounded and MUST NOT outlive its database. At most four databases are
  remembered; the least recently used is dropped when a fifth is opened, and an entry whose database file no longer
  exists is dropped on the next open.
- **FR-005**: `mcp bench` MUST distinguish the first call from the rest: one untimed call loads the snapshot, the
  timed samples follow it, and the summary reports the untimed call as `cold` beside the warm `p95`.
- **FR-006**: `013/spec.md`'s SC-005 MUST be restated with the pair this round measured — the warm p95 the
  criterion applies to and the cold call printed beside it — and the measurement MUST be recorded in
  `013/plan.md` beside the two earlier ones, in the same form.

## Success Criteria *(mandatory)*

- **SC-001**: On this repository's index (712 files, 13 686 passages, 350.2 MiB), a served call after the first
  costs **no more than 300 ms at p95** over the fixture's twelve queries — round 6's criterion, now met rather
  than recorded as missed. The round is not done until `mcp bench` prints that number.
- **SC-002**: The first call after a cold start is **published, not averaged away**: the harness prints its cost
  beside the warm p95, and the number is recorded in `plan.md` with the machine and the index size.
- **SC-003**: Nothing observable changes. Parity stays 12/12 over the fixture; the five tools answer the same
  text; a rebuild by another process is seen by the next call; the suite stays green.
- **SC-004**: Memory does not grow without bound. A process that opens five different indexes holds four
  snapshots and has dropped one, and the snapshot of a workspace whose database is gone is dropped on the next
  open.

**Measured at landing**, 2026-11-02, this machine, index as above: p95 **261 ms** and **248 ms** over two runs, cold
**2 641 ms** and **2 834 ms**, parity 12/12 both times. Round 6 published 3 199 ms for the same fixture.

## Assumptions

- One workspace per process, as round 6 assumed. The bound in FR-004 is what keeps that assumption from being
  load-bearing: four entries cover an agent, a bench and a rebuild in one process, and a fifth distinct index is
  a different program.
- The snapshot is the expensive thing and the handle is not. Measured above; if a future engine makes the open
  expensive instead, this round's premise needs re-measuring before the lease is re-examined.
- A snapshot of this repository costs tens of megabytes of managed memory rather than hundreds: the 350.2 MiB on
  disk is dominated by the engine's own vector representation, which the snapshot does not copy.

## Out of Scope

- **The build.** `002` T034 stays open: one writing run costs 119–132 s whatever it writes, and the index is 61×
  the source. This round makes the *read* affordable and says nothing about the write.
- **Answering without opening the database at all.** A file-stamp check could skip the one-millisecond open, and
  it would trade FR-002's guarantee for a twentieth of the cost. Refused, and named here so that it is a decision
  rather than an oversight.
- **A second transport, a second workspace, or a warm-up protocol the client knows about.** The server still
  answers `initialize` and then whatever it is asked; nothing in `initialize` changes.
- **Caching answers rather than the snapshot.** Two questions about one build share the snapshot; the round
  caches no result, no ranking and no rendered block.
- **`013` T034–T037** (resources, prompts, sampling, logging) and the rest of that round's open rows.


