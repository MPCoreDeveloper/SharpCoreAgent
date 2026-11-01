# Tasks — 021 image deduplication

Every row is closed by evidence: a test that fails without the change, or a `dotnet build` and `dotnet test` run
recorded in the commit that carries it.

## Phase 1 — the rule, in one place

- [X] T001 `ImageShown` (`Infrastructure/Images`): `Already(shown, picture)` — null means nobody said — and `Clause`, the
      one sentence the three doors wrap in their own shapes. `internal`, beside `ImagePayload`, for the same reason.
- [X] T002 `ToolInvocationContext.ImagesAlreadyShown` (`IReadOnlySet<ImageContent>?`) in `Domain.Tools`, documented as
      the conversation's pictures, with null as the safe default and an empty set as "looked, none yet", plus the two
      `PublicAPI.Unshipped.txt` entries.

## Phase 2 — the loop

- [X] T003 `CodingAgent.AlreadyShown(session)` collects `ChatMessage.Images` from the transcript at the start of a turn,
      with the remarks that say why it is derived rather than remembered.
- [X] T004 `TurnState` takes the set, adds every picture `Add` records, and answers `ImagesAlreadyShown`; `InvokeAsync`
      and `ExecuteAsync` carry it to the context beside the budget.
- [X] T005 `CodingAgentTests` — two calls in one round, then a second turn: the first call is told nothing, the second is
      told about the first picture, and the next turn's call is still told, with one carrier message in the transcript.

## Phase 3 — the three doors

- [X] T006 `view_image` reads, then answers a duplicate with a success sentence and the `Read` effect, then checks the
      budget; the remarks gain the fourth rule and the description one clause.
- [X] T007 `McpTool.Attach` takes the set, refuses a part the result already carries and a picture the conversation
      already carries, both before the budget, one line each in round 10's shape.
- [X] T008 `fetch_url`'s `Picture` answers a duplicate with the header block and a success that keeps the `Read` effect,
      before the budget, reusing the picture it built for the check.
- [X] T009 Door tests — `ViewImageToolTests` (+3: the duplicate sentence and effect; different bytes still travel; a
      duplicate beats a spent budget), `McpToolTests` (+2: the conversation's picture; the result's own repeat),
      `FetchUrlToolTests` (+1: the same body twice, with the effect kept).

## Phase 4 — the record

- [X] T010 `dotnet build -c Release` at 0 warnings, `dotnet test -c Release --no-build` at 1,309 passed / 0 failed.
- [X] T011 `README.md`: the image section gains deduplication beside the ceiling, the suite figure becomes 1,309, and the
      spec list has its round-12 entry.
- [X] T012 `specs/000-parity-roadmap.md`: row 12 in the rounds table, the round-12 section, the numbering note, and the
      open-work row for `017` narrowed — T031's deduplication clause is closed, its trimming and compaction clauses are
      not.
- [X] T013 `specs/017-image-view/tasks.md` T031 and `spec.md`'s out-of-scope row: deduplication is closed by `021`, and
      the stale clause saying compaction is undecided is corrected — `/compact` shipped in `003`, and what is undecided
      is *automatic* compaction, which `003` T034 already names.
