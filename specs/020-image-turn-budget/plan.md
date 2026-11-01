# Plan — 020 image turn budget

The spec is the argument. This is the shape it took, the choices that were weighed, and the numbers the build
produced.

## The shape, in one paragraph

`ImageBudget` (`Infrastructure.Images`) holds one predicate — does this picture fit in what is left — and one
sentence, `Spent`, that names what was left and what the picture costs and says that a new turn starts fresh.
`ToolInvocationContext` gains `ImageBytesRemaining` (`long?`), the number a tool may still attach, null meaning nobody
stated a budget. `AgentOptions.MaxTurnImageBytes` is the ceiling (16 MiB, `0` off, validated 1..64 MiB) and lives in
`Application`, where every other per-turn budget lives. `CodingAgent.TurnState` sums `ImageContent.ByteCount` over
every result that actually carried pictures, and `ExecuteAsync` puts `ceiling − spent` on the context of each call.
The three doors read it and refuse with `ImageBudget.Spent` in the shape each already uses.

## Decisions

**D1 — the loop owns the number, because only the loop sees the turn.** A tool is given one call and knows nothing
about its neighbours; after round 10 that is three tools, in one round, in one turn, on one session. `TurnState` is
the only object that has seen all of them, and it already exists for the verify gate and the spend totals. Anything
else — a static counter, an ambient service, a tool that asks the session — would be a second place where "how much
has this turn shown" is answered, and the first bug would be the two answers disagreeing.

**D2 — a number crosses the boundary, not a policy.** `Domain` is the constitution's "BCL only, no vendor types, no
policy about the outside world" layer, and `ToolInvocationContext` is where that boundary is felt. So the ceiling
stays in `Application`, and `Domain` learns only that a tool may be told how many bytes are left. The tool then decides
what it alone may attach, which is what it already does for its own result; handing it the ceiling would invite a tool
to compute a different remainder and to be right about a different question.

**D3 — the budget is in bytes, not in pictures.** A count is the cheaper rule and the wrong one: four 4 MiB
photographs and four 8 KiB diagrams are both four pictures and differ by a factor of five hundred. The thing that
gets a request refused is a size, so the count of pictures is not what is counted. The per-result count from round 10
survives because it answers a different question — what one answer may show a developer — and the two coexist.

**D4 — 16 MiB, and the arithmetic behind it.** Base64 spends four characters on every three bytes, so 16 MiB of
picture is 21.3 MB in the request; the smallest request limit this program targets is 32 MB, which leaves room for the
transcript and the tool schemas beside it. It is also exactly four pictures at `TextLimits.MaxImageBytes`, so the worst
one *result* can produce still fits, and a turn of ordinary screenshots — a few hundred kilobytes each — fits dozens.
The upper bound on the ceiling is 64 MiB, which is 85 MB of base64: a number no provider accepts is not a setting,
it is a bigger failure later.

**D5 — the estimate is the one round 10 already ships.** `ImageContent.ByteCount` is `base64 length / 4 * 3`, which is
`ImagePayload.EstimateBytes` spelled differently. Round 4's sink ceiling, round 9's per-image ceiling and this round's
per-turn ceiling are therefore one rule with three call sites, and a picture cannot be "4 MiB" to one of them and
something else to another. It is an estimate — the last base64 group's padding makes it a byte or two generous — and
generous is the right direction for a ceiling.

**D6 — the budget is the last reason each door checks.** The order is: an unaccepted type, then the per-image ceiling,
then the per-result count, then the turn. Each of those is a fact about this result and can be fixed by this result;
the turn is the one a tool cannot fix, and a refusal that names it while the real problem was `image/bmp` would send
the model in the wrong direction. It also keeps the round-10 messages unchanged for every case they already covered.

**D7 — the doors keep their own shapes.** `view_image` answers a failure before it opens the file: there is no point
encoding a picture that cannot travel, and the tool's whole job is to answer with a picture or a reason.
`McpTool` keeps a success and appends a line, because the server did its work and the description of it is still worth
reading — the sentence says the picture did not travel, which is what round 10 already does for a bmp. `fetch_url`
answers a failure with the header block intact, because what a fetch returns on failure is what it fetched and why it
stopped, which is the shape `008` chose. Three shapes, one sentence, because each was already the right shape.

**D8 — no flag and no settings key.** `MaxContextMessages` — the other limit that bounds one request — has neither,
and reaching it is how a host asks for more room. A CLI flag would be a second way to say what `AgentOptions` says,
and the round that adds it would have to decide precedence between the flag, the file and the record. Not this slice.

**D9 — no deduplication.** A repeated screenshot spends the budget twice, which is a real cost and the obvious next
optimisation. It was left out for a reason that is not laziness: dropping the second copy has to be *said*, since a
picture that silently did not arrive is the failure this whole line of work exists to prevent, and a note per
duplicate is a second sentence with its own UX question (two pictures and one sentence, or one picture and two?). That
is a slice of its own. The budget bounds the damage in the meantime.

## Rejected alternatives

| Alternative | Why not |
|---|---|
| A per-turn *count* of pictures | A count does not bound a request: four photographs at 4 MiB are sixty-four times four diagrams, and both are "four". D3. |
| Filtering in `AdvanceAsync` after the tools returned | The tool result would then say a picture is attached when it is not, and the console would show a different record from the model's. Round 10's rule is that the tool says what it did. |
| Reserving the round's budget before running its calls | A tool's result size is unknown until it runs; reserving would have to guess, and guessing is how a call that would have fitted gets refused. |
| Putting the ceiling on the context instead of the remainder | Leaks a policy into `Infrastructure` and lets a tool compute a different answer to the same question. D2. |
| Deduplicating identical pictures | Worth doing, and not instead of this: a turn of fifteen *distinct* screenshots is the case that fills a request. D9. |
| Making the ceiling a share of the model's context window | A window is tokens and a picture is priced in pixels; the two are not the same function in any provider's documentation, and not every catalogued model carries a window. |
| Ending the *turn* when the budget is spent | A turn ends on an answer or a spent budget, not on one tool's answer. The turn continues; one picture does not travel. |

## Complexity Tracking

Nothing to record. The change adds one static class with two members, one nullable property on an existing record, one
option with its validation, one `long` field on `TurnState`, one parameter through two private methods, and three
guards at three existing check points. No new project, no new dependency, no new public type in `Domain` beyond a
property, and no behaviour change for a picture that fits.

## As built

| File | Change |
|---|---|
| `src/SharpCoreAgent.Infrastructure/Images/ImageBudget.cs` | new: `Fits(bytes, remaining)` and `Spent(bytes, remaining)` |
| `src/SharpCoreAgent.Domain/Tools/ITool.cs` | `ToolInvocationContext.ImageBytesRemaining` (`long?`, default null) |
| `src/SharpCoreAgent.Domain/PublicAPI.Unshipped.txt` | the getter and the init setter, in order |
| `src/SharpCoreAgent.Application/AgentOptions.cs` | `MaxTurnImageBytes` = 16 MiB, its remarks, its validation, the summary bullet |
| `src/SharpCoreAgent.Application/CodingAgent.cs` | `TurnState._imageBytes`, `ImageBytesRemaining(ceiling)`, the sum in `Add`, `InvokeAsync`/`ExecuteAsync` carry the turn and the remainder |
| `src/SharpCoreAgent.Infrastructure/Tools/ViewImageTool.cs` | the guard before the file is read, the third rule in the remarks, one clause in the description |
| `src/SharpCoreAgent.Infrastructure/Mcp/McpTool.cs` | `Attach` takes the remainder, decrements it per picture, appends the line |
| `src/SharpCoreAgent.Infrastructure/Tools/FetchUrlTool.cs` | the guard in `Picture`, keeping the header block |
| `tests/…/Application/AgentOptionsTests.cs` | +2 — the default, the edges, both refusals |
| `tests/…/Tools/ViewImageToolTests.cs` | +2 — the `0 B` refusal, the exact-fit boundary and the unstated budget |
| `tests/…/Infrastructure/McpToolTests.cs` | +2 — nothing left, and the in-result decrement |
| `tests/…/Tools/FetchUrlToolTests.cs` | +1 — 8 KiB against 1 KiB, and the helper grew an optional budget |
| `tests/…/Application/CodingAgentTests.cs` | +2 — two calls in one round, and a new turn; plus a `ContextTool` that can read its context |

## What it printed (2026-10-31, this repository)

- `dotnet build .\SharpCoreAgent.slnx -c Release` — **0 warnings, 0 errors**.
- `dotnet test .\SharpCoreAgent.slnx -c Release --no-build` — **1,302 passed, 0 failed, 0 skipped**, 17 s. Round 10 left
  1,293, so the nine new cases are the whole delta and nothing else moved.
- No live provider run. Round 9 proved the wire — an `image_url` part carrying a `data:` URI, against a real model and
  a real PNG — and this round changes which bytes reach that message rather than how they are sent. A run would not
  prove the thing being claimed either: the ceiling is a decision about a request's size, and the arithmetic in D4 is
  checkable without a key.

## What a later round would want to know

- The budget counts what a turn *adds*. A session's earlier messages keep their pictures, so a very long session is
  still bounded only by `MaxContextMessages`. The row that closes that is `017` T031's trimming/compaction clause, and
  it needs the decision this product has not made.
- The refusal is the only place the number reaches a developer today. If `/cost` ever grows an image line, this is the
  estimate to reuse rather than a second rule beside it.
- The ceiling is one number on one option. If a host ever needs to vary it per model — a model with a small window
  wanting less — the place to do that is `AgentOptions` composed per descriptor, not a second field on the context.
