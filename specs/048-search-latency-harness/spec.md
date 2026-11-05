# Feature Specification: Search latency harness

**Feature**: `048-search-latency-harness`
**Status**: implemented 2026-11-04

## Problem

`002` SC-003 says a `search_code` call on a 20,000-passage index answers under 250 ms at p95. The index on this
repository held 8,206 passages and no harness existed, so the criterion was recorded as unmeasured. The first synthetic
run also exposed a real defect: the engine's `FullTextIndex.Search` cost 613 ms p95 at 20,400 passages.

## What ships

- A skipped-by-default xUnit harness, `SearchLatencyTests`, that builds 20,400 passages from 1,700 generated C# files and
  issues 100 `search_code` calls.
- `SHARPCOREAGENT_RUN_LATENCY=1` runs it; without the variable the test reports itself skipped, so the ordinary suite
  stays 17 seconds.
- A replacement lexical leg: `LexicalIndex`, an immutable in-memory BM25 index built from the already-stored
  `chunks.tokens` strings. The database, schema and snapshot cache are unchanged.
- The measured p95 is asserted against 250 ms, so a future regression fails the harness rather than being noticed later.

## Measured result

On this machine, .NET 11 Release:

| Corpus | Passages | Samples | p50 | p95 | Max | Result |
|---|---:|---:|---:|---:|---:|---|
| 1,700 generated files | 20,400 | 100 | 25.8 ms | **50.8 ms** | 903.0 ms | pass |

The first sample includes the snapshot's cold load and is the max; p95 is the criterion.

## Functional requirements

- **FR-001**: The harness is skipped unless `SHARPCOREAGENT_RUN_LATENCY=1`.
- **FR-002**: The corpus is generated, so it has no fixture to drift.
- **FR-003**: The harness builds through `RebuildAsync`, not by inserting rows.
- **FR-004**: It asserts at least 20,000 passages and p95 < 250 ms.
- **FR-005**: The lexical leg still returns the same kind of hits, ordered by score and id, and feeds the existing ranker.
- **FR-006**: The custom index is built only from `chunks.tokens`; no new table or package is introduced.
- **FR-007**: A query with no token, no postings or `k <= 0` returns an empty list.

## Non-goals

- Changing the vector, graph or ranking legs.
- Adding a database table or a new package.
- Running the 20,000-passage build in CI.

## Success criteria

- The skipped test is reported as skipped in the ordinary suite.
- The measured p95 is under 250 ms and the measurement is recorded in `002`.
- Existing retrieval, ranking and tool tests remain green.
