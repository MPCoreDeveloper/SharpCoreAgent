# Feature Specification: One Commit Per Checkpoint

**Feature Branch**: `026-checkpointed-index-writes`

**Created**: 2026-11-02

**Status**: Accepted 2026-11-02 (constitution 1.2.0). Round 17 of `specs/000-parity-roadmap.md`. It takes the first of
the three levers `002`'s `research.md` D21 named and leaves the other two where they are.

**Input**: User description: "`002` T034's next slice: a checkpoint interval between commits, keeping the per-file
failure unit bounded rather than removing it."

## Why

The index was measured for the first time in `002` D21, and the split said something the whole plan had been guessing
at. A rebuild of this repository — 698 files, 350.2 MiB, 17 m 51 s — spent 15 m 47 s in `write`, and of that, 15 m 49 s
was the store's commit:

> The commit also has a price per batch: this build makes one per written file plus one each for `remove`, `edges` and
> `meta`, which is 700 batches in the rebuild … 1.36 s, 2.25 s and 2.5 s per batch. So a commit costs more the bigger
> the index is, and the build pays it once per file.

That is why a cold build costs 1.53 s per file here rather than the 1.12 s D16 measured on a 14 MiB index, and why
SC-002's 5 000 files are "hours rather than 89 minutes". The write is not the write: the **commit** is.

T034 was written as "batch the row writes (`InsertBatch` is in the engine's surface)", and D21 retired that premise
along with the assumption behind it. The rows are already written in one batch per file; it is the batch's *end* that
costs, and batching rows harder inside one commit changes nothing. What changes everything is asking the store to
commit less often.

### The property that must survive

The per-file batch was not an accident. `IndexFileWriter` says so in its own remarks:

> The unit of work is one file, deleted and re-inserted inside one batch, because that is the largest thing that can
> fail independently: a build that dies on file 40 leaves files 1–39 committed and file 40 as it was.

Removing that property to buy speed is the trade this round refuses. So the unit becomes a **checkpoint**: a bounded
group of files that reaches the store in one commit. A build that dies leaves everything up to its last checkpoint, so
the re-work it costs is bounded by the checkpoint's size instead of by one file — a bounded amount of re-work for a
bounded multiple of the speed, which is exactly the shape D21 asked for.

Nothing else about a build's guarantees moves. `meta` is still written last, in its own batch, so a build that dies
mid-checkpoint leaves the previous `build_id` in place and the index reads **stale** — never wrong, because what a
reader sees is whole files, whole checkpoints, and one complete `meta` row set away from being readable at all.

### What this round is not

D21 named three levers. This is the first. `communities` (1 m 49 s for 26 files, 2 m 0 s for 6 — a fixed charge per
*writing* run) and the no-op `meta`/open overhead (5.5 s for a run that changed nothing) are the second and the third,
and each is a slice of its own. SC-006's 61x index size belongs to D17–D19 and is not touched here either.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — A rebuild costs minutes, not a quarter of an hour (P1)

A developer types `sharpcoreagent index --rebuild` on a repository this size. Today the command spends a quarter of an
hour asking the store to commit 700 times. After this round it asks 26 times, and the walk, the extraction, the vectors
and the clustering are the run instead of the rounding on top of a commit.

**Why this priority**: it is the whole slice. SC-002's cold half misses on corpus size, and it misses by this factor.

**Independent test**: one `--rebuild` of this repository's index with `--verbose`, quoting the phases and the total
against D21's table.

**Acceptance scenarios**:

1. **Given** this repository's index (712 files, 13 686 passages, 350.2 MiB), **when** `index --rebuild --verbose` runs
   to completion, **then** the run reports no more than ⌈files ÷ 32⌉ + 2 commits of the store and its `write` phase
   plus nested `commit` are at least five times cheaper than D21's 15 m 47 s and 15 m 49 s.
2. **Given** the same index and a workspace where a few files changed, **when** `index` runs, **then** the write phase
   commits once for the files it wrote, and what remains of the run is the community pass D21 named — the commit is no
   longer part of why a writing run is slow.

### User Story 2 — A build that dies keeps what it already stored (P1)

The machine is suspended, the terminal is closed, the store throws, or the caller's budget runs out. Whatever the build
had committed up to its last checkpoint is in the index, and the index answers with the previous build's status rather
than with a half-written one.

**Why this priority**: speed that costs a corrupt index is not speed. This is the property the round must not spend.

**Independent test**: a writer whose next file throws, abandoned mid-checkpoint, leaving the store's row count exactly
where the last commit left it; and a budget that expires mid-checkpoint, leaving the rows it already wrote and a
`partial` state.

**Acceptance scenarios**:

1. **Given** an open checkpoint carrying one written file, **when** the next file throws, **then** the checkpoint is
   abandoned and the store holds neither file's rows — the state it was in after the last commit.
2. **Given** a build whose budget expires between two files inside a checkpoint, **when** the run stops, **then** the
   checkpoint is committed, the counters name what was written, and `meta` records `partial`.
3. **Given** a build that stops on cancellation, **when** the token is raised between two files, **then** the
   checkpoint is committed before the exception leaves the writer.

### User Story 3 — The write path has one rule, not two (P2)

The remove phase and `index --prune` delete rows one file at a time through the same batch machinery, so pruning a
hundred vanished files pays a hundred commits on a large index. The rule this round introduces — a bounded group of
writes reaches the store once — applies to them too. Every caller of `IndexFileWriter` that is not a build keeps its
present single commit per operation.

**Why this priority**: it is the same lever in the same code path. Leaving it out would make the round's own sentence
("the commit is the write") true for writing files and false for deleting them.

**Independent test**: a prune of three vanished files leaves two file rows, and the row set afterwards is what the
per-file deletes produced.

**Acceptance scenarios**:

1. **Given** five indexed files and three deleted from disk, **when** `PruneAsync` runs, **then** three rows are gone,
   two remain, and the set is identical to what the per-file deletes left.
2. **Given** a build that sees files vanish, **when** it runs, **then** the vanished set is removed in one commit
   rather than one per file.

### Edge Cases

- **A checkpoint that writes nothing commits nothing.** The batch is opened by the first write inside the checkpoint,
  so a budget that expires before the first file costs no commit at all — the no-op run D21 measured at 5.5 s gets no
  worse.
- **A build with fewer files than one checkpoint** is one checkpoint: 5 files commit once, not ⌈5 ÷ 32⌉ plus a remainder.
- **An empty workspace** commits once, for `meta`, exactly as today.
- **A file recorded as skipped or as an error** writes a `files` row and therefore counts towards the checkpoint: the
  bound is on the store's work, not on the kind of row.
- **A cancellation before the first file** leaves the store untouched and no checkpoint behind: abandoning a
  checkpoint that never opened a batch is a no-op, not an error.
- **A second begin while a checkpoint is open** is a programming error and throws rather than nesting two batches.

## Requirements *(mandatory)*

- **FR-001**: A build MUST reach the store once per bounded checkpoint of files rather than once per file. The bound is
  **32 files**, and every write a build makes — a file's rows, a skipped file's placeholder, the vanished set — is
  carried by the open checkpoint.
- **FR-002**: The checkpoint MUST be the failure unit. An exception inside one MUST abandon it — the rows it buffered
  discarded, no part of it in the store — and the build MUST fail as it does today, with the report lost and the writer
  lock released.
- **FR-003**: A build stopped by its budget or by cancellation MUST commit the checkpoint it holds before it stops, so
  what it wrote is kept and its counters are true.
- **FR-004**: `meta` MUST remain the last write of a build and MUST remain its own commit, so an interrupted build
  leaves the previous `build_id` and the index reads stale rather than wrong.
- **FR-005**: `PruneAsync` and a build's vanished set MUST each remove their rows in one commit for the whole set,
  rather than one commit per file.
- **FR-006**: A caller that opens no checkpoint MUST see today's behaviour: `Write`, `WritePlaceholder`, `Delete`,
  `WriteEdges` and `WriteMeta` each commit their own work exactly once.
- **FR-007**: One batch MUST be open at a time and nothing MUST be read from the database while it is open. The build's
  own reads — the stored rows, the id sequence, the edge resolver, the community pass — all happen on committed state.
- **FR-008**: The number of commits a build made MUST be observable to the code, to its tests
  (`IndexWriteResult.Commits`) and to a developer running `index --verbose`, so the arithmetic this round claims is
  counted rather than inferred from seconds. No CLI flag, no `--json` key and no published report shape changes: a
  commit count is a fact about this writer's strategy, not about the index it left behind.

## Success Criteria *(mandatory)*

- **SC-001**: One `index --rebuild --verbose` on this repository's index reports **no more than ⌈files ÷ 32⌉ + 2**
  commits, where *files* counts every file the build records a row for — indexed and skipped alike — and the two are the
  edges and `meta`. That is 26 for the 754 files here, against D21's 700, and the count is recorded in `plan.md` beside
  the phases.
- **SC-002**: The same run's `write` phase and the nested `commit` are at least **5× cheaper** than D21's 15 m 47 s and
  15 m 49 s on the same machine and the same index, with both runs published side by side.
- **SC-003**: Nothing observable changes. The rows, counters and hashes a build leaves are the same; `--verify` answers
  healthy; the five served tools answer the same text and parity stays 12/12; an unchanged run still writes nothing but
  `meta`; a budget-stopped run is still `partial` and finishes on the next call; the suite stays green.
- **SC-004**: The failure unit stays bounded and named: a test abandons a checkpoint mid-build and finds the store
  exactly as the last commit left it, and a budget that expires mid-checkpoint keeps what the checkpoint wrote.

**Measured at landing**, 2026-11-02, this machine, this repository's index (740 files, 14 200 passages, 525.2 MiB): the
run is **2 m 53 s** and reports **26** commits, against D21's 17 m 51 s and 700. `write` **36.9 s** and the commit inside
it **37.5 s**, against 15 m 47 s and 15 m 49 s — 25× both. `communities` is now 74 % of the run and did not move. The
table and the terminal's own output are in `plan.md`.

## Assumptions

- **32 is arithmetic, and the measurement revised it rather than confirming it.** D21 priced a commit at 1.36 s against
  about 1.5 s for a file, which made 32 files look like a balanced group. After the change the per-file *write* is 49 ms
  (36.9 s across 754 files) and one commit is 1.44 s, so at 32 files a checkpoint the commit is still about 48 % of the
  write phase: a larger group would help further. What bounds it is the batch's buffer rather than the re-work — 32 files
  cost about 1.6 s of writing to redo, and 128 of them would cost the store several megabytes it has to hold — so 32 is
  where this round stopped, and the next pull of that lever is recorded as open rather than taken quietly.
- **The store's batch is a buffer.** `BeginBatchUpdate` holds rows until `EndBatchUpdate` flushes them and
  `CancelBatchUpdate` discards them, which is what makes an abandoned checkpoint leave nothing behind. The round's
  tests assert this rather than trusting the documentation.
- **Holding a batch open across a file read is safe.** The build's only await inside a checkpoint is reading one file's
  text; the batch is a buffer on the database object and the loop is single-threaded, so nothing else can enter the
  store while it is open.
- **A coarser commit does not make a concurrent reader wrong.** A reader already sees a build in progress as stale, and
  it already sees whatever was committed a moment ago; the only thing that changes is how much a moment is worth.

## Out of Scope

- **`communities` and the no-op `meta`/open overhead.** D21's second and third levers, each a slice of its own. A
  writing run stays minutes long after this round because the community pass is a fixed charge, and that is the next
  round's subject rather than a disappointment to be papered over here.
- **Making the store's commit cheaper, or writing the index in a format that does not need one.** That is the engine's
  business and a different program.
- **A flag for the interval.** `--checkpoint <files>` would move to the command line a decision the code can make once,
  and every caller of a build would then have a way to make it badly.
- **Parallel extraction.** The commit is the cost and the loop is single-threaded on purpose; making extraction
  concurrent before the commit is fixed would optimise the wrong 12 %.
- **The 5 000-file synthetic tree and SC-002's restatement.** T034's closing condition needs that corpus and both of
  its halves; this round moves one half and says by how much.
- **SC-006's index size (61×).** D17–D19's subject, untouched.

