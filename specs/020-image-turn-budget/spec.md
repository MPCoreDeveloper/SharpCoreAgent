# Spec — 020 image turn budget

**Status**: landed on `main` (2026-10-31) in one commit, with no merge commit — the shape rounds 6, 9 and 10 took.
Spec number `020`: `018` is still held for `web_search` by a sentence in `specs/000-parity-roadmap.md`.

**Depends on**: `001` (`AgentOptions`, `CodingAgent`'s turn loop, `TurnState`), `017` (`ImageContent`,
`ToolResult.Images`, the loop's hand-over), `019` (`ImagePayload`, the three doors and the ceilings each of them
already carries), `008` and `009` (the two doors that were opened last round).

## Why this round exists

Round 10 put a ceiling on every door and none of them on the turn. `view_image` attaches one picture, a tool result
carries at most four, and one picture is at most 4 MiB. All three are ceilings on a *result*, and the quantity that
decides whether a provider accepts a request is none of them.

An attached picture is not sent once. It joins the transcript, and every later request in the same turn carries it
again — so a turn's cost is the sum of everything it has shown so far, multiplied by the rounds that follow. Nothing
was counting that sum. A browser turn of fifteen rounds, each returning four pictures at the per-image ceiling, ends
with sixty pictures in its final request: 240 MiB of picture, 320 MB of base64, refused by the provider with a status
code whose body says nothing about images. The developer reads "the provider rejected the request" and has no way to
know which of the round's tool calls did it.

`019`'s own out-of-scope list named this and deferred it: *"images in session files, a per-turn budget,
deduplication, a token estimate — round 9's reasoning stands."* Round 9's reasoning was about the *transcript*: it
grows and stays grown, and what would bound it is compaction, which this product has not decided on. That is still
true and is still open. It is not this: this round bounds **one turn's additions**, which is a request-size question
with an answer that needs no compaction feature, no provider and no new decision about what a session file is.

The other half of the argument is that the three doors cannot answer it between them. A tool knows its own result and
nothing about its neighbours; `TurnState` is the only object that sees the whole turn. Last round's shape — one rule,
several doors — therefore needs the rule to take a number from the loop rather than to read one from a config file.

## What it adds

| Surface | Layer | Shape |
|---|---|---|
| `ImageBudget` | `Infrastructure.Images` | one predicate (`Fits`) and one sentence (`Spent`) — the only place the three doors agree on either |
| `ToolInvocationContext.ImageBytesRemaining` | `Domain.Tools` | what a tool may still attach, or null when nobody said |
| `AgentOptions.MaxTurnImageBytes` | `Application` | 16 MiB per turn, zero as the documented off switch, validated at both ends |
| `CodingAgent.TurnState` | `Application` | counts what travelled, hands each call the remainder |
| `view_image`, `McpTool`, `fetch_url` | `Infrastructure` | each refuses a picture that does not fit, with both numbers and the way out |

## Requirements

- **FR-001** One ceiling, on the turn: `AgentOptions.MaxTurnImageBytes`, default 16 MiB of picture *before* base64.
  Zero turns it off; a non-zero value is validated between 1 byte and 64 MiB, because 1 byte is a legitimate "no
  pictures this turn" and 64 MiB is 85 MB of base64 that no provider this program targets accepts with text beside
  it. The default is 21.3 MB of base64 — inside the 32 MB request limit of the smallest provider documented here, and
  exactly four pictures at `TextLimits.MaxImageBytes`, which is the worst one result can produce.
- **FR-002** The budget is counted in decoded bytes, using the same estimate `ImagePayload` already applies to an MCP
  part (`base64 length / 4 * 3`), so the per-image ceiling and the per-turn ceiling cannot disagree about what a
  picture costs.
- **FR-003** The loop counts only what travels. `TurnState.Add` adds the images a result actually carries, and the
  count is read before each call, so the second tool of a round sees what the first one spent — not what the whole
  round spent, which would refuse a picture that fits.
- **FR-004** The number reaches the tool as `ToolInvocationContext.ImageBytesRemaining`, and null is the documented
  "nobody stated a budget": a context built by hand, a test, a host that fills nothing in. Null refuses nothing, which
  is the same safe default `Model` takes for the capability gate, and is what keeps every round-9 and round-10
  expectation byte-identical.
- **FR-005** Each door refuses with both numbers and the way out, in `ImageBudget.Spent`'s words: `view_image` answers
  a failure before it reads a byte, an MCP result stays a success and appends one line per un-attached picture, and
  `fetch_url` answers a failure that keeps the header block saying what was actually fetched.
- **FR-006** The budget is the last reason checked, after the ones that belong to the result — an unaccepted type, a
  payload over the per-image ceiling, a result that already carries four. A refusal names the reason that actually
  applies, so a wrong diagnosis is not the first thing the model reads.
- **FR-007** The ceiling is per turn. A new turn on the same session starts with all of it, which is what makes the
  refusal's advice — ask again in a new turn — something the model can act on rather than a dead end.
- **FR-008** Nothing else changes. The artefact sink still writes the file and still names the path, attachment is
  still additive, a session file still keeps text and not pictures, and no request shape moves.

## Success criteria

- **SC-001** `AgentOptionsTests` — the 16 MiB default beside the other sized-for-work defaults, zero accepted as the
  switch, one byte and 64 MiB accepted as the edges, 64 MiB + 1 and −1 refused with the parameter name.
- **SC-002** `ViewImageToolTests` — a turn with nothing left is refused with `0 B of image budget left`, `the picture
  is 11 B` and the fresh-budget sentence, and attaches no image; a budget equal to the file attaches it, and a context
  that states no budget still attaches, which is the behaviour every earlier test already relies on.
- **SC-003** `McpToolTests` — a result whose only picture does not fit stays a success, carries no image and names the
  budget in one appended line; two pictures against a four-byte budget attach the first and name the second, which is
  the in-result decrement no other test reaches.
- **SC-004** `FetchUrlToolTests` — an eight-kibibyte PNG against a one-kibibyte budget is a failure naming the type,
  the size, what was left and the fresh-budget sentence, with no image.
- **SC-005** `CodingAgentTests` — two calls in one round see ten then seven bytes of a ten-byte ceiling, and one
  picture reaches the transcript; a second turn on the same session sees the whole ten again.
- **SC-006** `dotnet build .\SharpCoreAgent.slnx -c Release` at 0 warnings and `dotnet test` at 1,302 passed, 0 failed
  — nine new cases on top of round 10's 1,293.

## Out of scope, on purpose

- **Transcript trimming, compaction, deduplication.** `017` T031 keeps them open, and `003` T034 is the same gap from
  the console's side. This round bounds what one turn *adds*; a long session whose earlier messages each carry a
  picture still grows, and the answer to that is compaction, which is a decision about what a conversation is.
- **An image token estimate before the request.** `017` T032 stays refused for the reason it gave: a provider bills
  the pixels it decodes, the number arrives in `Usage` after the call or not at all, and inventing one would make
  `/cost` a guess. A budget in bytes is a measurement; a budget in tokens would be a claim.
- **A flag or a settings key for the ceiling.** `MaxContextMessages` has neither, and the same argument applies: a
  host that wants a different number sets it in `AgentOptions`. Adding `--max-turn-image-bytes` would be a third way
  to configure one turn, beside the file and the record.
- **Downscaling or re-encoding to fit.** A picture over the budget is not silently made smaller: the file is the
  developer's, and a recompressed screenshot is a different picture from the one that was taken.
- **Counting what earlier turns attached.** The ceiling is a turn's; the requests of a long session still carry the
  pictures of the turns before it. Naming that in the README rather than hiding it is the honest version, and the work
  it needs is the row above.
- **Serving anything.** `mcp serve`'s published set does not move; no served tool can attach a picture.

## Stop gates

Work stops and asks if any of these turns out true: **G1** the ceiling has to live in `Domain` as a policy rather than
crossing the boundary as a number (it does not — `ToolInvocationContext` carries a `long?` and knows nothing about
where it came from); **G2** preserving round 9 and round 10 means giving `ImageBytesRemaining` a non-null default (it
does not — null means "unstated", which refuses nothing); **G3** the default needs a live provider run to justify it
(it does not — the number is documented as a default inside a published limit, not as a promise about any provider,
and the arithmetic is 16 MiB × 4/3 = 21.3 MB against a 32 MB limit). None of G1 or G2 was hit; G3 was answered by
arithmetic and by the round-9 measurement that a `data:` URI already travels.

