# Spec — 021 image deduplication

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–11 took. Spec number `021`:
`018` is still held for `web_search` by a sentence in `specs/000-parity-roadmap.md`, and `020` was round 11.

**Depends on**: `017` (`ImageContent`, `ChatMessage.Images`, the loop's hand-over), `019` (`ImagePayload` and the three
doors that attach a picture), `020` (`ImageBudget`, the per-turn ceiling, and the shapes a door's refusal takes).

## Why this round exists

Round 9 let a picture into a conversation and wrote down what was still missing, in its own words: *"two `view_image`
calls on one file cost two pictures"*. Round 11 then bounded what one turn may add — 16 MiB — and named this as the next
slice, because a ceiling bounds the spend without reducing it. Both rounds left the same sentence in `017` T031.

The waste is easy to describe. A browser turn asks a server for the same page twice, or a model asks for a screenshot
it has already seen, and each call attaches the same bytes again: the transcript carries two copies, and every request
after the second pays for both. Ten rounds of the same 400 KB screenshot is 4 MB of picture and 5.3 MB of base64
re-sent on every request after the tenth — a quarter of round 11's whole budget spent on pixels the model has already
looked at. Round 11's refusal cannot help here: nothing is over the ceiling, and asking again in a new turn is exactly
what makes it worse.

## What it adds

| Surface | Layer | Shape |
|---|---|---|
| `ToolInvocationContext.ImagesAlreadyShown` | `Domain.Tools` | the pictures the conversation already carries, or null when the caller did not say |
| `ImageShown` | `Infrastructure.Images` | one predicate (`Already`) and one clause (`Clause`) — the only place the three doors agree on either |
| `CodingAgent.AlreadyShown` | `Application` | derives the set from the transcript at the start of each turn |
| `CodingAgent.TurnState` | `Application` | adds each picture it records, so the next call in the same round is told |
| `view_image`, `McpTool`, `fetch_url` | `Infrastructure` | each answers a duplicate with a sentence instead of a second copy |

## Requirements

- **FR-001** A picture the conversation already carries is not attached a second time, by any of the three doors that
  can attach one.
- **FR-002** Identity is the picture — its media type and its bytes — and never the name it arrived under. Two URLs may
  serve one picture, and one path may hold different bytes by the time it is read again; only the bytes are what a
  provider is charged for and what the model sees.
- **FR-003** The loop derives the set from the transcript rather than remembering it in a side table, so the three cases
  that would otherwise need a special case are right by construction: a compacted conversation has dropped its older
  messages and may show those pictures again; a resumed one carries none, because a session file holds no images; and a
  turn that attaches a picture adds it as it goes.
- **FR-004** The set reaches the tool as `ToolInvocationContext.ImagesAlreadyShown`, and null is the documented "nobody
  said": a context built by hand, a test, a host that fills nothing in. Null attaches everything, which is the same safe
  default `Model` and `ImageBytesRemaining` take, and is what keeps every round-9, round-10 and round-11 expectation
  byte-identical.
- **FR-005** A duplicate is answered as a **success**, not a refusal, in all three doors: the request is met, because the
  picture is in a message the model can see. A refusal would say the opposite — that the model cannot see it — and a
  model told that describes what it imagines.
- **FR-006** The duplicate check comes before the turn's budget, because a duplicate spends nothing and a budget
  refusal would name a ceiling that stopped nothing. In `view_image` that means after the file is read: a duplicate
  cannot be recognised without its bytes.
- **FR-007** An answer that repeats a picture it has already given is the same rule at the finest scope: one MCP result
  attaches a repeated part once and says so.
- **FR-008** Nothing else changes. No new option, no flag, no settings key, no persistence, no request shape, and no
  change to how a picture that is not a duplicate travels.

## What it does not do

- **Not transcript trimming or compaction.** The transcript still grows and stays grown; `/compact` remains the only way
  to drop messages, and automatic compaction is `003` T034. This round stops a picture being *added* twice; it does not
  remove one that is already there.
- **Not a cache of encoding work.** A second `view_image` on the same unchanged file still reads and encodes it, because
  that read is what says the bytes are the same. A path-keyed cache would answer "unchanged" for a file that was
  rewritten in place.
- **Not a picture count, and no token estimate.** Round 11's reasoning stands: bytes are what a provider refuses on.
- **Not deduplication across a reload.** A session file keeps text and not pictures, so a resumed conversation carries
  none and re-attaches them — which is the honest answer rather than a promise the transcript cannot keep.

## Evidence

Seven new cases: `ViewImageToolTests` (a duplicate is a success with a sentence and a `Read` effect; a file whose bytes
changed still travels; a duplicate beats a spent budget), `McpToolTests` (a picture the conversation carries is named
rather than sent; an answer that repeats a picture attaches it once), `FetchUrlToolTests` (the same body twice is a
success with the header block and one effect) and `CodingAgentTests` (two calls in one round, then a second turn: the
first call is told nothing, the second and the next turn's call are told about the picture). One case is deliberately
missing: no test asserts a *path* is remembered, because nothing is.
