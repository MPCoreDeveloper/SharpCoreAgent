# Tasks — 017 image view

Legend: `[X]` implemented and covered by tests in this change; `[ ]` refused on purpose, kept here so the next pass
has a real list rather than a memory.

## Phase 0 — the shape, decided before it was written

- [X] T001 Put the image on `Domain.Conversation` rather than in the transport: `ImageContent` (media type, base64,
      `ByteCount`, `ToDataUri`) and `ChatMessage.Images` / `HasImages` / `User(text, images)`. The round exists
      because round 4 said this is a content-block change in `Domain`, so anything less would have been a wire hack.
- [X] T002 `ToolResult.Images` and `ToolResult.Image(callId, toolName, content, image)`, with the remark that says
      where the images go and why: the tool role carries text only.
- [X] T003 `ModelDescriptor.SupportsImages`, default `false`, in the same declared-capability style as
      `SupportsTools`. Refusing before a request is spent is the pattern the type already had.

## Phase 1 — the tool

- [X] T004 `ViewImageTool` in `Infrastructure.Tools`: `PathSafety.TryResolveRead`, the extension table
      (`.png`/`.jpg`/`.jpeg`/`.gif`/`.webp`, case-insensitive), the ceiling, the capability gate, then one read.
- [X] T005 `TextLimits.MaxImageBytes` = 4 MiB, with the note that base64 spends a third on top and a provider bills
      pixels whether the file is compressed or not.
- [X] T006 Every refusal a `ToolResult` and none an exception, in the order extension → empty → ceiling → model →
      read, so a text-only session is never told about a file it could not have read.
- [X] T007 The `Read` effect, workspace-relative, on the root that owns the file — the attached-root rule from round 1.
- [X] T008 Registration in `AddTools` beside the other reads: eighteen default tools, no flag adds it and none
      removes it.
- [X] T009 Tests: `ViewImageToolTests`, eleven methods.

## Phase 2 — the hand-over

- [X] T010 `CodingAgent` attaches `ChatMessage.User(PromptLibrary.ImageAttached, result.Images)` after the tool
      message, generically: the loop learns nothing about `view_image`.
- [X] T011 `PromptLibrary.ImageAttached` — the sentence that survives a reload and says to call `view_image` again.
- [X] T012 `OpenAiContent.ImageDataUris` plus the converter: a string when there are no images (every request this
      build sent before the round is byte-identical), an array of parts when there are.
- [X] T013 Tests: `CodingAgentTests.AnImageAToolReturnsArrivesOnTheNextUserMessage` (order, `HasImages` on both
      messages, the image in the second request) and `OpenAiCompatibleTransportTests.AUserMessageWithAnImageIsSentAsParts`.

## Phase 3 — the flag the tool consults

- [X] T014 Grok's catalogue: `grok-4.7`, `grok-4` and `grok-3` read images; `grok-3-mini` does not. DeepSeek's
      catalogue: none. `OpenAiCompatibleChatModel.DescriptorFor`: yes, because that provider is the developer's own
      endpoint.
- [X] T015 `models` prints `images=yes|no` beside `tools=yes|no`.
- [X] T016 Tests: `ModelCatalogTests.OnlyTheModelsDeclaredToReadImagesSaySo`.

## Phase 4 — the paper, and what the round does not touch

- [X] T017 Operating rule 9 in `PromptLibrary.SystemPrompt`, unconditional like rule 8: every host registers the tool.
- [X] T018 `README.md`: the tool list, the `view_image` paragraph, the `images=` note under the model catalogues,
      and the session-file paragraph.
- [X] T019 `specs/001-coding-agent-core/contracts/tools.md`: the catalogue row, the schema, and a section pointing at
      `contracts/view-image.md`.
- [X] T020 `specs/017-image-view/contracts/view-image.md`, `spec.md`, `plan.md`, this file, and the round's row plus
      section in `specs/000-parity-roadmap.md`.
- [X] T021 `McpServerPublishedSetTests` gains one `InlineData`: `view_image` is registered and **not** published, so
      round 6's five-tool allow-list is unchanged.
- [X] T022 Build and suite: `dotnet build .\SharpCoreAgent.slnx -c Release` at 0 warnings, `dotnet test` 1256/1256,
      the count `README.md` carries. `ChatLoopTests`' ceiling moved 1 700 → 2 000 because the prompt grew.
- [X] T023 Write down what the round leaves open, below, rather than discovering it a round later.

## Refused, and why

- [ ] T024 **Images in MCP results.** `McpContent.Flatten` describes an `image` part as a MIME type and a size and
      keeps doing so; round 4's `contracts/mcp-content.md` is untouched. A screenshot the browser wrote is shown by
      calling `view_image` on the path the sink returned, which is one tool call and no second sink. It would
      reverse if a server returned an image with no file behind it — a stream, or a URL — because then there is
      nothing on disk to name.
- [ ] T025 **`fetch_url` returning images.** Round `008`'s contract refuses a non-text content type by name, and a
      picture fetched from the network is a second capability with its own ceiling and its own address rules.
- [ ] T026 **OCR, local vision, captioning.** The bytes go to the model the developer chose. A local decoder is a
      dependency, a licence and a second opinion nobody asked for.
- [ ] T027 **PDF, SVG, audio and video.** Five extensions are the whole table; a format whose extension would lie is
      refused rather than relabelled.
- [ ] T028 **Resizing, re-encoding, rotating.** Show the file or refuse it with the number that says why; silently
      rewriting a developer's picture is worse than a refusal.
- [ ] T029 **Images in session files.** Deliberate, and documented in `JsonFileSessionStore`: a transcript people
      grep is not a blob store, and the file is one `view_image` call away.
- [ ] T030 **Pasted, dragged or piped images, and an `--image` flag on `run`.** Attachment input is a different
      program (`007` T030–T032 says the same thing about roots), and a flag would be a second way for a picture to
      enter a conversation with no path in the transcript.
- [ ] T031 **A per-turn image budget, deduplication, or transcript trimming.** The transcript grows and stays grown;
      what would bound it is a compaction feature this product has not decided on — `003` T034 is the same gap seen
      from the other side.
- [ ] T032 **An image token estimate before the request.** A provider bills the pixels it decodes; the number
      arrives in `Usage` after the call or not at all, and inventing one would make `/cost` a guess.
- [ ] T033 **Serving `view_image` over `mcp serve`.** The published set is a compile-time allow-list of the five
      read-only index tools. Serving a picture is serving a file's bytes to a process the developer named, which is
      a different argument from serving the index's evidence — and one nobody has made yet.
