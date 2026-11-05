# Contract: Search latency harness

| Item | Value |
|---|---|
| corpus | 1,700 generated C# files, 20 methods each |
| passages | 20,400 |
| samples | 100 `search_code` calls |
| statistic | `ceil(0.95 * samples) - 1` after sorting |
| target | p95 < 250 ms |
| gate | `SHARPCOREAGENT_RUN_LATENCY=1`; otherwise skipped |
| build path | `SharpCoreAgentIndex.RebuildAsync` |
| lexical input | `chunks.tokens` only |
| BM25 | `k1 = 1.2`, `b = 0.75`, score rounded to 6 places |
| ordering | score descending, then chunk id ascending |

The harness fails if the corpus is below 20,000 passages or p95 is at least 250 ms.
