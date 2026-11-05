# Implementation Plan: Search latency harness

## The harness

`SearchLatencyTests` generates 1,700 files with 20 methods each. The passage chunker produces 20,400 passages (the
class header and its methods do not split one-to-one with the methods). `RebuildAsync` writes them through the same
writer the CLI uses. The harness then calls `SearchCodeTool` 100 times with distinct method names, records wall time
around the tool call, sorts, and takes the element at `ceil(0.95 * n) - 1`.

The test calls `Assert.Skip` unless the environment variable is set, so it is visible in the ordinary suite and does
not pay the three-minute build in CI.

## The optimization

The engine's `FullTextIndex` was the cost: its general-purpose analyzer/search made a query over 20,000 postings take
hundreds of milliseconds. The index already stores the exact space-joined tokens the writer produced, so
`LexicalIndex.Build` reads those strings once and builds:

- a token → postings list (`chunk id`, term frequency),
- document lengths and their average.

`Search` tokenises the query the same way (`CodeTokenizer.TokenizeForQuery` has already done it), computes BM25 with
`k1 = 1.2`, `b = 0.75`, sums per token, rounds to six places, and orders by score then id. The result is one dictionary
lookup per query token plus a sort of the matches, not a scan of every passage.

`IndexReader.LoadLexical` now fills that structure; `IndexSnapshot.Lexical` and `LexicalRetriever` keep the same shape,
so the ranker, budget and tools are untouched.

## Why not a new table

The stored token strings are already the input. A second table would duplicate them, need migration, and reintroduce
the staleness problem `IndexReadCache` already solves.

## Tests

The existing retrieval suite plus the new skipped harness. The ordinary run is 1,655 passed and 1 skipped; the explicit
run is 1 passed at p95 50.8 ms.
