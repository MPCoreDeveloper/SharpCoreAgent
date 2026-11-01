# Tasks — 020 image turn budget

Every row is closed by evidence: a test that fails without the change, or a `dotnet build` and `dotnet test` run
recorded in the commit that carries it.

## Phase 1 — the rule, in one place

- [X] T001 `ImageBudget` (`Infrastructure/Images`): `Fits(bytes, remaining)` with null meaning "nobody stated a
      budget", and `Spent(bytes, remaining)` — both numbers, and the sentence that says a new turn starts fresh.
- [X] T002 `ToolInvocationContext.ImageBytesRemaining` (`long?`) in `Domain.Tools`, documented as the loop's number
      and null as the safe default, with `PublicAPI.Unshipped.txt` gaining the getter and the setter in order.
- [X] T003 `AgentOptions.MaxTurnImageBytes` = 16 MiB with the remarks that justify the number, the summary bullet, and
      validation: zero is off, 1..64 MiB otherwise, both refusals naming the parameter.

## Phase 2 — the loop

- [X] T004 `TurnState` sums `ImageContent.ByteCount` in `Add`, so the total is of what travelled and not of what a
      tool offered, and answers `ImageBytesRemaining(ceiling)` — a number, or null when the ceiling is off.
- [X] T005 `InvokeAsync` and `ExecuteAsync` carry the turn and the remainder, and the context the tool receives is
      the only place the number appears. The approval path and the preview path are untouched: neither can attach.
- [X] T006 `CodingAgentTests` — two calls in one round see ten then seven of a ten-byte ceiling and one picture reaches
      the transcript; a second turn on the same session sees the whole ten again. A `ContextTool` reads its context,
      which `StubTool` deliberately does not.

## Phase 3 — the three doors

- [X] T007 `view_image` refuses before the file is read, with the relative path, the type, the size and what was left;
      the remarks gain the third rule and the description gains one clause about the turn's budget.
- [X] T008 `McpTool.Attach` takes the remainder, refuses after the three reasons that belong to the result, decrements
      per attached picture so a second picture in the same answer is judged against what the first spent, and appends
      one line per refusal in round 10's shape.
- [X] T009 `fetch_url`'s `Picture` refuses with the header block intact, after the type, the truncation and the
      per-image ceiling checks — the budget is the last reason.
- [X] T010 Door tests — `ViewImageToolTests` (+2: the `0 B` refusal and the exact-fit boundary, with the unstated
      budget still attaching), `McpToolTests` (+2: nothing left; the in-result decrement), `FetchUrlToolTests` (+1:
      8 KiB against 1 KiB), `AgentOptionsTests` (+2: the default and both edges, plus a negative).

## Phase 4 — the record

- [X] T011 `dotnet build .\SharpCoreAgent.slnx -c Release` at 0 warnings, `dotnet test` at 1,302 passed / 0 failed.
- [X] T012 `README.md`: the image section gains the turn's ceiling and the arithmetic behind it, the suite figure
      becomes 1,302 in both places, and the spec list has its round-11 entry.
- [X] T013 `specs/000-parity-roadmap.md`: row 11 in the rounds table, the round-11 section, and the open-work row for
      `017` narrowed — T031's budget clause is closed, its trimming and deduplication clauses are not.
- [X] T014 `specs/019-image-ingress/spec.md`: the out-of-scope line that deferred a per-turn budget now names the round
      that closed it, rather than contradicting the code.
- [X] T015 `specs/017-image-view/tasks.md`: T031 rewritten to say which of its three clauses landed and which did not,
      and why they are not the same question.

## Deliberately not done

- **Deduplication.** A repeated screenshot still spends the budget twice. Dropping the second copy has to be said in
  words, and that sentence is its own UX decision (`017` T031). The budget bounds the damage in the meantime.
- **Transcript trimming and compaction.** The ceiling is a turn's; earlier turns keep their pictures in the transcript.
  `003` T034 is the same gap seen from the console, and it is a decision about what a conversation is.
- **An image token estimate.** `017` T032's reason stands unchanged: bytes are a measurement and tokens before the
  call would be a guess that makes `/cost` one too.
- **A flag or a settings key.** `MaxContextMessages` has neither; a host that wants a different ceiling sets the
  option. Two ways to say one thing is how precedence bugs start.
- **A ceiling that varies by model.** One number today. If a model's window ever needs less, the place for that is a
  composed `AgentOptions`, not a second field on the context.
- **No live provider run.** The wire was proved in round 9 and this round changes which bytes reach it; the number is
  arithmetic against a published limit, not a claim about any one provider.
