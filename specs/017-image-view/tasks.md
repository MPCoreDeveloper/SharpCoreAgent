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
      (`.png`/`.jpg`/`.jpeg`/`.gif`/`.webp`, case-insensitive; `.svg` was added by T027's amendment and is rendered to
      PNG through `SvgImage`), the ceiling, the capability gate, then one read.
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

- [X] T024 **Images in MCP results — closed by `019` (round 10).** The round refused this because a screenshot the
      browser wrote is shown by calling `view_image` on the path the sink returned, and it named the condition that
      would reverse the decision: *a server returning an image with no file behind it*. `019` found the milder reason
      the refusal had missed — the bytes were already in memory, so the second call bought a copy and not a picture —
      and closed it without removing the sentence or the sink: an `image` part is now collected by
      `McpContent.FlattenWithImages`, attached by `McpTool` only when the session's model is declared to read images,
      and still described and still written to `.sharpcoreagent/artifacts/`, because that path is what a resumed
      transcript keeps. `contracts/mcp-content.md` stands: the text is byte-identical.
- [X] T025 **`fetch_url` returning images — closed by `019` (round 10).** The reasoning held for every binary and was
      wrong for exactly four: `019` attaches `image/png`, `image/jpeg`, `image/gif` and `image/webp` when the body
      arrived whole and the session's model can see, and keeps `008`'s refusal-by-name for everything else — which now
      names those four types instead of promising that the tool never downloads a binary. The ceiling is the same
      4 MiB a file goes through, and the fetch's own 2 MiB cut is refused rather than sent as half a picture.
- [ ] T026 **OCR, local vision, captioning.** The bytes go to the model the developer chose. A local decoder is a
      dependency, a licence and a second opinion nobody asked for.
- [X] T027 **SVG.** `view_image` and `--image` accept `.svg` and `SvgImage` renders it to PNG at its own size (at
      most 4096 px on either side) through Svg.Skia; the answer names the source and the rendering. PDF, audio and
      video remain refused: their extensions would lie about what a provider can read.
- [X] T027b **PDF.** `view_image` accepts `.pdf` and `PdfImage` renders one page (page 1 by default, `page` selects
      another) through PdfPig and SkiaSharp; no PDFium or Ghostscript process. Audio and video remain refused: their
      extensions would lie about what a provider can read and each needs a separate runtime.
- [ ] T027c **Audio and video.** Extracting frames or audio is a separate runtime and a separate contract; a video
      named as a picture is still refused.
- [X] T028a **Resizing and re-encoding, on request.** `view_image` takes optional `max_dimension` (64–4096); it
      preserves aspect ratio, re-encodes PNG and names the result. Omitting it is byte-identical to the old answer.
      `ImageResize` refuses an undecodable file or an out-of-range value instead of guessing.
- [X] T028b **Rotating.** `view_image` accepts optional `rotation` 90/180/270 and rotates clockwise after any
      rendering or downscale; arbitrary angles are refused. The answer names the rotation and the resulting size.
- [ ] T029 **Images in session files.** Deliberate, and documented in `JsonFileSessionStore`: a transcript people
      grep is not a blob store, and the file is one `view_image` call away.
- [ ] T030 **Pasted, dragged or piped images.** Piping is shipped in two parts: `--image <path>` in `047-run-image-input` and `--image -` (stdin) in `050-stdin-image-input`. Dragging and a clipboard API remain open, because each is a UI concern rather than a file path.
- [X] T031 **Transcript trimming.** Closed by `042-context-fraction-compaction`: `CompactAtContextFraction` defaults to 0.8, compacts before the request when the estimated prompt exceeds that fraction of the model window, keeps system prompts and the newest message, and never splits a tool round. `/compact [n]` remains the manual override. The per-turn image ceiling (`020`) and duplicate suppression (`021`) still bound what a turn adds.
- [ ] T032 **An image token estimate before the request.** A provider bills the pixels it decodes; the number
      arrives in `Usage` after the call or not at all, and inventing one would make `/cost` a guess.
- [X] T033 **Serving `view_image` over `mcp serve`. Closed by this amendment.** The published set now includes
      `view_image`: read-only, the same `PathSafety` and 4 MiB ceiling as a turn, and an `image` part in the MCP
      result rather than a server-side description. A model-free server context is the one case the tool's
      capability gate allows, because the client — not the server — chooses the model that reads the picture.
