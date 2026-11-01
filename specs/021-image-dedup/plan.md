# Plan — 021 image deduplication

The spec is the argument. This is the shape it took, the choices that were weighed, and what the build produced.

## The shape, in one paragraph

`ToolInvocationContext` gains `ImagesAlreadyShown` (`IReadOnlySet<ImageContent>?`) — the pictures the conversation
already carries, null meaning nobody said. `CodingAgent.AlreadyShown` collects them from `session.Messages` at the start
of a turn and hands the set to `TurnState`, which adds each picture `Add` records, so the second call of a round is told
about the first one's picture before any request has carried it. `ImageShown` (`Infrastructure.Images`) holds the one
predicate and the one clause the three doors share, next to the `ImageBudget` that holds the turn's sentence. Each door
answers a duplicate with its own shape — a success line, a bracketed note, a header block — and attaches nothing.

## Decisions

**D1 — identity is the picture, not the name.** A `view_image` on `shots/a.png` and an MCP server returning the same
bytes are the same picture, and a path that was rewritten in place is a different one. Bytes plus the normalised media
type is the only identity that is true of both, and it is the same identity a provider is charged by. The type is part of
it only because every door normalises the spelling first — `image/jpg` is attached as `image/jpeg` — so two pictures that
compare unequal on type are two a provider is told different things about.

**D2 — the set is derived from the transcript, not remembered.** The obvious alternative is a `HashSet` on the session
that grows as pictures are attached. It would be wrong three times: `/compact` drops messages and the set would not know,
a resumed session has no pictures at all and the set would claim some, and a turn's own additions would have to be added
in a second place that can drift from `session.Add`. Deriving it from `Messages` at the start of a turn costs one string
hash per picture already in the transcript, holds references rather than copies, and is right about compaction, resume
and turn boundaries without a special case for any of them.

**D3 — an empty set and a null set are different, and both are safe.** Null is "nobody said" — a context built by hand,
a test, a host that never filled it in — and refuses nothing, exactly as `ImageBytesRemaining` and `Model` do. An empty
set is the loop saying it looked and the conversation holds no picture yet. The two behave identically for a first
picture, which is what keeps every existing expectation byte-identical.

**D4 — a duplicate is a success, not a refusal.** Round 11's three shapes are all refusals because the picture is
nowhere: it did not fit, its type is not accepted, its result already carried four. A duplicate is the opposite: the
picture is exactly where the model asked for it to be, one message earlier. Answering it as a failure would tell a model
that cannot be trusted with silence that it cannot see something it can see, and the model would describe what it
imagines instead of reading the earlier message. So `view_image` succeeds with a sentence and no image, an MCP result
keeps its success and appends a line, and `fetch_url` succeeds with the header block intact on the same URL.

**D5 — the duplicate check goes before the budget, which reverses round 11's ordering for one door.** Round 11's D7 had
`view_image` refuse before it opened the file, because there is no point encoding a picture that cannot travel. A
duplicate cannot be recognised without the bytes, so the read moves ahead of both checks and the order becomes: type,
per-image ceiling, model gate, read, duplicate, budget. The cost of the reversal is one read of at most
`TextLimits.MaxImageBytes` that round 11 avoided in the narrow case where the turn is already spent; the gain is that a
duplicate is shown even when the budget is empty — it costs nothing — and that a refusal names the reason that applies,
which is round 11's own principle (`020` D6) applied to a fourth reason.

**D6 — an answer that repeats itself is the same rule at a finer scope.** One MCP result can carry several parts, and a
server that returns the same screenshot twice is doing what a model does across rounds. `Attach` checks the list it is
building before it checks the conversation, because the local fact is the cheaper one and states the more precise thing:
*this result already carries the same picture* rather than *the conversation does*.

**D7 — the identities are compared as records, in a hash set.** `ImageContent` is a record over `MediaType` and
`Base64`, so `HashSet<ImageContent>` compares strings and holds references to the strings already in the transcript — no
digest to compute, no second copy of the payload, and no cryptographic dependency in a type that carries bytes. The
comparison is one string hash per lookup and an exact ordinal compare on the rare hash hit, which is what keeps a
conversation with sixty pictures in it linear rather than quadratic in megabytes.

**D8 — no new surface.** No option, no flag, no settings key, no `/dedup`, nothing persisted. The ceiling round 11 added
is the only knob, and deduplication needs none: it is a fact about what already happened, not a policy about what may.

## The three shapes, written down

| Door | Duplicate answer | What the model reads |
|---|---|---|
| `view_image` | success, no image, `Read` effect kept | `'shots/shot.png' (image/png · 400,000 bytes) is not attached again: the same picture is already in this conversation, attached to an earlier message. That earlier message is the copy to read — asking for the same file twice shows nothing new.` |
| MCP `image` part | success, description kept, one line appended | `[image/png: not attached — the same picture is already in this conversation, attached to an earlier message]` |
| `fetch_url` | success, header block, one `Read` effect | `… the response is image/png, 390.6 KiB, and it is not attached again: the same picture is already in this conversation … Cite the URL; the earlier message carries the picture.` |
| MCP part repeating its own result | success, one line appended | `[image/png: not attached — this result already carries the same picture]` |

## What the build produced

`dotnet build -c Release` at 0 warnings, 0 errors; `dotnet test -c Release --no-build` at 1,309 passed, 0 failed — 1,302
plus the seven cases this round adds. The one thing that changed for an existing case is the order of two checks in
`view_image`, which no existing test observes: a call that is over the budget and is not a duplicate still answers the
same refusal, with the same two numbers and the same advice.

## What was deliberately not built

- **A path-keyed memory.** It would answer "unchanged" for a file rewritten in place, and it would compare a name to a
  picture — the two are not the same question.
- **A digest.** It would buy nothing a record comparison does not already give, and it would put a cryptographic type
  between a payload and the provider that accepts it.
- **A message index in the sentence.** "Attached to message 7" would be more precise and less useful: the transcript is
  read top to bottom, and `/compact` renumbers nothing a model can see.
- **Deduplication across `/compact`.** Compaction drops messages, so a picture it dropped is no longer in the transcript
  and is attached again. That is the honest reading of "the conversation carries it", and the alternative — remembering
  what was dropped — is the side table D2 rejected.

