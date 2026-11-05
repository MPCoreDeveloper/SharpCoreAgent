# Tasks: Search latency harness

- [x] T001 Add the generated 20,400-passage harness, skipped unless `SHARPCOREAGENT_RUN_LATENCY=1`.
- [x] T002 Measure the pre-change p95: 613.1 ms (miss).
- [x] T003 Replace the engine lexical leg with `LexicalIndex` BM25 over `chunks.tokens`.
- [x] T004 Measure the post-change p95: 50.8 ms (pass).
- [x] T005 Run the ordinary suite: 1,655 passed, 1 skipped.
- [x] T006 Record the result in `002` spec/research/roadmap.
- [x] T007 Build, full suite and recorded evaluation.

## Evidence

```text
latency: files=1700 passages=20400 samples=100 p50=25.8 ms p95=50.8 ms max=903.0 ms
```

## Out of scope

CI execution of the 20,000-passage build, vector/graph/ranking changes, new tables or packages.
