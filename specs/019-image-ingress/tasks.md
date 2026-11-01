# Tasks — 019 image ingress

Every row is closed by evidence: a test that fails without the change, or a `dotnet build` and `dotnet test` run
recorded in the commit that carries it.

## Phase 1 — the rule, in one place

- [X] T001 `ImagePayload` (`Infrastructure/Images`): `IsImage`, `TryMediaType` (five spellings onto four types,
      `image/jpg` → `image/jpeg`, parameters stripped), `EstimateBytes` (`Length / 4 * 3` minus visible padding),
      `TryCreate` (type → data → ceiling, each refusal a sentence), and `AcceptedList` as the one string a refusal
      quotes.
- [X] T002 `TextLimits.MaxImagesPerResult` = 4, with the reason it is a number rather than "whatever the server sent".
- [X] T003 `McpArtefactSink.Estimate` becomes a one-line delegation to `ImagePayload.EstimateBytes`, so the sink's
      ceiling and the attach ceiling are one rule with two call sites.
- [X] T004 `ImagePayloadTests` — the spelling theory, the closed-list theory, `IsImage`, the estimate over padded and
      unpadded base64, the boundary (accepted at the ceiling, one byte over refused with both numbers), the refusal
      naming the type and the accepted list, and a part with no data.

## Phase 2 — the MCP door

- [X] T005 `McpContent`: `McpImage` (type + payload, undecoded), `McpFlattened`, and `FlattenWithImages` — the text is
      byte-identical to `Flatten`, and every `image` part with data is collected in the server's order.
- [X] T006 `McpCallResult.Images`, defaulted to `[]` so every failure path and both existing `Flatten` overloads keep
      compiling and behaving.
- [X] T007 `McpServerConnection.CallAsync` returns the flattened text and the parts through the same `save` delegate.
- [X] T008 `McpTool.Attach`: the model gate (declared text-only → nothing attached, one sentence saying so; no model at
      all → nothing attached and nothing added), the ceiling, the count, and one appended line per un-attached picture
      naming its reason.
- [X] T009 `McpToolTests` — the picture attached beside the description, the text-only sentence, a bmp named with its
      reason, and five pictures attaching four and naming the fifth.

## Phase 3 — the `fetch_url` door

- [X] T010 `FetchUrlTool.AnswerAsync` takes the invocation context; an `image/*` response goes to `Picture` before the
      text path.
- [X] T011 `Picture`: the closed table, the whole body, the image ceiling, the model gate — each refusal keeps the
      header block and names the number that stopped it; success is `ToolResult.Image` with a `Read` effect on the
      final URL.
- [X] T012 The non-text refusal is reworded: it names the four picture types and says the body was not attached,
      instead of promising that `fetch_url` never downloads a binary. Tool description and class remarks follow.
- [X] T013 `FetchUrlToolTests` — a PNG attached with its header block and effect, `image/jpg` normalised, `image/svg+xml`
      and `application/pdf` still refused by name, a text-only session refused with the model named, and a body cut at
      the fetch ceiling refused with both numbers. The one existing assertion that quoted the old sentence moved with it.

## Phase 4 — the hand-over

- [X] T014 `PromptLibrary.ImagesAttached` for a result carrying more than one picture, and `ImageAttached` reworded to
      name the rule ("call the tool again — view_image if the picture is a file on disk") rather than one tool as if it
      were the only door.
- [X] T015 `CodingAgent` chooses between the two by count, on the message it already wrote.
- [X] T016 `CodingAgentTests` — two pictures in one user message with the plural sentence, in the tool's order, in the
      request the model received; the single-picture test still expects the singular one.
- [X] T017 `PromptLibraryTests` — both sentences say the same thing about a reloaded transcript and neither ships in the
      system prompt.

## Phase 5 — the record

- [X] T018 `dotnet build .\SharpCoreAgent.slnx -c Release` at 0 warnings, `dotnet test` at 1,293 passed / 0 failed.
- [X] T019 `README.md`: the `view_image` section gains the two other doors, the `fetch_url` paragraph stops promising
      that nothing binary is downloaded, the current suite figure is 1,293, and the spec list has its round-10 entry.
- [X] T020 `specs/000-parity-roadmap.md`: row 10 in the rounds table, the round-10 section, the open-work row for `017`
      narrowed to what is genuinely still open, and the note that `018` is still held for `web_search`.
- [X] T021 `specs/017-image-view/{spec,tasks}.md`: T024 and T025 closed with the reason, and the "out of scope" lines
      that said MCP images and `fetch_url` images were not this program's business corrected rather than left to
      contradict the code.
- [X] T022 `specs/008-web-fetch/tasks.md`: the refusal-by-type contract annotated with the round that narrowed it.

## Deliberately not done

- **The `Append` line has no ceiling of its own.** At most four notes, each under 150 characters, appended after
  `MaxResultChars` has already bounded the text. A fifth bound would be a second thing to keep in step.
- **`McpContent` collects every image part, unbounded.** The cap is applied by the tool, which is the only place that
  knows the model; a server that returned two hundred pictures would have them referenced, not copied, and four would
  travel.
- **No `Accept: image/*`.** Round 2's header is unchanged, so what a page returns to a text fetch is unchanged.
- **No live provider run.** Round 9 proved the wire (`image_url` with a `data:` URI) against a real model with a real
  PNG. This round changes which bytes reach that message, and every path is covered by a test; re-proving the wire
  would measure round 9 again.
