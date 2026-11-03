# Implementation Plan: The Write-Back Without a Parser

**Feature**: `028-row-addressed-symbol-writes` · **Spec**: [spec.md](spec.md) · **Tasks**: [tasks.md](tasks.md)

Round 19. One slice: `assign` stops issuing 7 305 statements and writes the same two columns by primary key, in the same
batch, with the same commit.

## Summary

One loop changes shape and one constant disappears. `CommunityAssigner.Write` keeps its batch, its two `commit`
measurements and its single counted commit, and inside that batch it hands each row to `IndexSql.Update` — the parser-free
write `IndexFileWriter.WriteMeta` has used for `meta` keys since the meta table existed. `UpdateSymbol` loses its last
user and is deleted.

Nothing else moves. Same rows, same labels, same centrality, same numbering, same counters, same report, same schema, same
`--json`. The change is inside one `for` loop of one private method.

## Decisions

### D1 — The row API is the write, and the statement goes

The probe measured 3 001 rows in one batch twice: 12 875–14 483 ms through `ExecuteSQL` with bound parameters, 86–143 ms
through `UpdateByPrimaryKey`. Both write the same two columns of the same table, both are counted by the same
`EndBatchUpdate`, and the difference is what the statement path does before the store sees the row: format every value back
into statement text, then parse the text, then bind it again.

This repository already made that argument once, for inserts, and wrote it down where the next reader would find it
(`IndexSql.Insert`'s remark, `Services/SqlLiteralScanner.cs`, known issue 7). It has never been made for updates, and
`meta` used the row path only because it was convenient. Now the busiest write in the index uses it too, and the statement
is deleted rather than kept "for reference": one way to write those two columns.

### D2 — One dictionary per row, not one shared bag

The obvious allocation win — build the bag once and overwrite its two entries 7 305 times — is rejected. The store hands
`UpdateByPrimaryKey` a dictionary and the batch holds the row until it flushes; whether it copies the values at call time
or keeps the reference is not documented and not observable from here without depending on it. If it keeps the reference,
a shared bag gives every row the last row's values, and the symptom would be 7 305 declarations in one community — a
plausible-looking index that answers nonsense.

7 305 small dictionaries cost about a millisecond. Buying a dependency on undefined behaviour to save a millisecond, in the
step whose slowness is the subject of the round, would be the wrong trade twice over.

### D3 — The probe's claim becomes three assertions that stay

The probe is deleted, and the three facts it relied on are tests in `IndexSqlUpdateTests`:

1. A row-addressed update inside a batch is stored by `EndBatchUpdate`.
2. The same update, cancelled, leaves the previous value — the promise `CommunityAssigner`'s one batch makes to an
   interrupted pass, made about a different entry point into the same batch.
3. A row-addressed update naming two columns of a wide row leaves the other eleven alone — the difference between an
   update and a row replace, which is the only way this change could corrupt an index.

A fourth case keeps the pass honest at its own level: a build over the two-cluster corpus stores the same labels and
centrality as before, and a declaration's `name`, `qualified_name`, `kind`, `doc`, `signature` and line range are byte
identical after the pass.

### D4 — The batch, the commit and the counters are not touched

`Write` still opens one batch, still charges both ends to `commit`, still returns one commit, and `CommunityAssignment`
still carries the counters `IndexWriter` adds. Round 18 spent its length making those numbers true; this round spends none
of it re-opening them. The only diff in the pass is the loop body and a deleted constant.

### D5 — What this round does not do

- **`louvain` (53.4 s).** It becomes the largest step of `communities` when this lands, and it is the store's. Making it
  smaller means a coarser graph or a bounded candidate set, which is a decision about label stability — a spec of its own.
- **Dropping `idx_symbols_community`.** The probe's residual (86–143 ms for the table, including index maintenance) says
  the lever is not needed, and dropping an index changes the plan of every query that reads a community.
- **Batch size.** The batch was never the suspect: 7 305 statements in one batch cost 11 ms each, and the probe's 3 001
  statements in one batch cost 4.5 ms each — the per-statement cost does not move with the batch.

## Approach

1. `CommunityAssigner.Write` — the loop body becomes `IndexSql.Update(_database, IndexTable.Symbols, symbols[index].Id,
   new Dictionary<string, object>(StringComparer.Ordinal) { ["community"] = …, ["centrality"] = … })`; `UpdateSymbol` is
   deleted; the method's remarks say why the row path is the one the pass takes.
2. `IndexSqlUpdateTests` — the three store facts, on a scratch index built with `IndexTestDatabase` (D3).
3. `CommunityAssignerTests` — one case that a build leaves the columns the pass does not own untouched (D3, fourth case).
4. `dotnet build` and `dotnet test` in Release.
5. One `index --rebuild --verbose` on this repository, the five numbers published, and `assign` compared against round 18's
   1 m 21 s.
6. `specs/002-code-evidence-index/research.md` gains the decision with both measurements; `specs/000-parity-roadmap.md`
   gains round 19.

## Measured

The probe, before the change — 3 001 declaration rows in one index, one batch each way, the same values and the same seven
labels, each run twice:

| 3 001 rows, one batch | cold | warm |
|---|---|---|
| `ExecuteSQL` with bound parameters | 14 483 ms | 12 875 ms |
| `IndexSql.Update` by primary key | 86 ms | 143 ms |

The rebuild, after it — 761 files, 14 287 passages, 7 311 symbols, 10 840 edges, 623 communities, 525.2 MiB:

| step | round 18 | round 19 |
|---|---|---|
| `nodes` | 0.1 s | 0.0 s |
| `adjacency` | 0.2 s | 0.0 s |
| `louvain` | 53.4 s | 1 m 8 s |
| `degree` | 0.0 s | 0.0 s |
| `assign` | 1 m 21 s | **1.8 s** |
| `communities` | 2 m 15 s | 1 m 10 s |
| the run | 3 m 2 s | 1 m 56 s |
| commits | 27 | 27 |

NFR-001 asked for `assign` below 5 s: 1.8 s. NFR-002 asked for `commits` not to move: 27. The remaining 0.25 ms per row is
index maintenance, which the statement path paid too. `index` reports `state fresh` at 6.0 s for a run that changed
nothing; `index --verify` finds 761 rows for 761 files, 0 hash mismatches, 0 orphans.

## Complexity Tracking

No violation, and nothing added. The round deletes a constant, a statement and one class of work; it adds no type, no
parameter, no member on a published record, no flag and no dependency. The one new risk — that the row API treats a batch
differently from a statement — is bought down by D3's tests rather than by an argument.
