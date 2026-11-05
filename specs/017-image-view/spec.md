# Spec — 017 image view

**Status**: landed on `main` as `e69cbdc` (2026-10-31), whole suite 1256/1256 at 0 warnings. Spec number `017`:
`009`–`011` are reserved by `specs/000-parity-roadmap.md` and `012`–`016` were taken, so `017` was the next free
one. It is the round `010`'s T035 predicted and left for its own round. It landed in one commit with no merge
commit, so there is no branch name to record — the commit is the record.

**Depends on**: `001` (the loop, `ChatMessage`, `ToolResult`, the tool seam), `010` (the screenshots the browser
round writes under `.sharpcoreagent/artifacts/` — the files this round can finally show), and `013` (the served
set, which this round must not widen).

## Why this round exists

A turn can list a directory, read a file and, since round 4, drive a browser that leaves a PNG in the workspace.
None of that answers the question the screenshot exists to answer:

> What is in this picture?

`read_file` cannot answer it. Its answer is a numbered listing of text, so a binary file is refused rather than
rendered as mojibake. `McpContent.Flatten` cannot answer it either: an `image` part is described as
`image/png · 36,912 bytes`, which is a sentence about a picture rather than the picture. And a model asked to
work from that sentence does the one thing worse than refusing — it invents what the picture probably showed.

The missing piece was never a writer. Round 4 already wrote the file, correctly, and said why it would not go
further: *"a screenshot the model could \*see\* is a content-block change in `Domain.Conversation`, which is its own
round"* (`specs/010-browser/tasks.md`, T035). This is that round. `ToolResult` gained `Images`, `ChatMessage`
gained `Images` on the user role, and one tool turns a path into both.

The second reason is symmetry with `dotnet_verify`. A turn that edits `.cs` cannot end on a claim; it ends on a
tool result. A turn asked what a screenshot shows should not end on a description of a file name either.

## What it adds

| Surface | Layer | Shape |
|---|---|---|
| `ImageContent` | `Domain.Conversation` | `MediaType`, `Base64`, `ByteCount`, `ToDataUri()` — the bytes, not a path |
| `ChatMessage.Images` / `HasImages` / `User(text, images)` | `Domain.Conversation` | a user message carries pictures beside its text; no other role can |
| `ToolResult.Images` / `ToolResult.Image(...)` | `Domain.Tools` | a tool answers with a picture and still describes it in text |
| `ModelDescriptor.SupportsImages` | `Domain.Model` | declared per model, `false` by default |
| `view_image` | `Infrastructure.Tools` | the tool: read-only, one `path`, six extensions, one ceiling; SVG is rendered to PNG |
| the hand-over | `Application.CodingAgent` | after the tool message, a user message carrying `PromptLibrary.ImageAttached` and the image |
| rule 9 and `ImageAttached` | `Application.PromptLibrary` | the guidance and the sentence, shipped with the tool |
| `OpenAiContent.ImageDataUris` | `Infrastructure.Chat` | `image_url` parts with a `data:` URI; text-only messages stay strings |
| `images=yes` / `images=no` | `SharpCoreAgent.Cli` | what `models` prints, because the flag is now load-bearing |
| the eighteenth tool | `SharpCoreAgent.DependencyInjection` | `view_image` in `AddTools`, no flag adds or removes it |

## Requirements

- **FR-001** A user message MAY carry images beside its text (`ChatMessage.Images`, `HasImages`), and no other role
  MAY. The tool role stays text-only because every compatible provider rejects an image part there.
- **FR-002** A tool result MAY carry images (`ToolResult.Images`). When it does, the loop appends a user message
  carrying `PromptLibrary.ImageAttached` and those images, *after* the tool message that produced them, so the
  transcript reads: the call, the sentence, the picture.
- **FR-003** `view_image` is registered in a build that configures nothing, is `ToolSafety.ReadOnly` so a plan
  turn and a `readonly` session call it without a prompt, takes exactly one argument `path`, and reports a `Read`
  effect naming the file workspace-relative.
- **FR-004** The extension alone decides the media type the provider is told: `.png` → `image/png`, `.jpg`/`.jpeg`
  → `image/jpeg`, `.gif` → `image/gif`, `.webp` → `image/webp`, case-insensitive. `.svg` is rendered to PNG at its
  own size by `SvgImage` before it is attached, because no provider accepts `image/svg+xml`; the rendered PNG obeys
  the same 4 MiB ceiling and the answer names the source size and the rendering. Anything else is refused by
  name, listing those six extensions and `read_file` for text. The bytes are never sniffed: a provider rejects a
  mislabelled payload, and guessing would only move the failure later.
- **FR-005** The file is read only under the ceiling (`TextLimits.MaxImageBytes`, 4 MiB), checked from the file's
  length before a byte is read. A refusal states the size and the ceiling, and asks for a smaller copy.
- **FR-006** A session whose model is not declared `SupportsImages` is refused *before* a byte is read, and the
  refusal names the model (`provider:id`) and says in as many words that the model must not describe the picture.
  A context that named no model at all is refused the same way, as `the session's model`: no model is not
  permission to see.
- **FR-007** The hand-over is a `data:` URI in an `image_url` part
  (`{"type":"image_url","image_url":{"url":"data:image/png;base64,…"}}`). A message with no images is still
  serialised as a plain string, so every request this build sent before this round is byte-identical after it.
- **FR-008** The capability is declared, never probed: `ModelDescriptor.SupportsImages` defaults to `false`, the
  catalogues say which entries can read a picture, and `models` prints `images=yes|no` for each. No test reaches a
  live endpoint and none needs a key.
- **FR-009** A session file keeps the transcript's text and not its images. The `ImageAttached` sentence is what
  survives a reload, and it says that the way to see the picture again is one more `view_image` call.
- **FR-010** `mcp serve` publishes none of this. The allow-list stays the five read-only index tools, and
  `view_image` is asserted registered-but-not-served, so a later round that wants to serve it has to say so.
- **FR-011** The prompt ships with the tool or not at all: operating rule 9 of `PromptLibrary.SystemPrompt` names
  `view_image`, when to call it, and that a refusal is answered by saying so rather than by describing a picture
  the model was never shown. No flag adds or removes either the tool or the rule.

## Edge cases

| Case | What the model is told |
|---|---|
| path outside the workspace | `ToolResult.Refused` from `PathSafety` — the same sentence every tool gives |
| a directory | `'<path>' is a directory. Use list_directory to see what it contains.` |
| no such file | `no file at '<path>'` |
| not one of the six extensions | the six extensions, and `read_file` for text |
| a zero-byte file | `'<path>' is empty (0 bytes), so there is nothing to show.` |
| over the ceiling | the file's size, the ceiling and the 4 MiB reading, and a request for a smaller copy |
| model declared text-only | the model's name, "declared text-only in this build", and "Do not describe what the image contains" |
| no model in the context | the same refusal, phrased `the session's model` |
| an attached root | readable, named relative to the root that owns it (round 1's rule) |
| a tool other than `view_image` returns images | the loop attaches them too: the hand-over is a property of `ToolResult`, not of one tool |
| two pictures in one round | one user message per result, each after its own tool message |
| a resumed transcript | the sentence without the picture, and the sentence says to call `view_image` again |

## Success criteria

- **SC-001** `ViewImageToolTests` — eleven test methods: the hand-over (media type, base64, the sentence, the `Read`
  effect), a theory over the six spellings of the four media types, and one refusal for each case above that the
  tool itself owns.
- **SC-002** `CodingAgentTests.AnImageAToolReturnsArrivesOnTheNextUserMessage` — the tool message carries no image,
  exactly one user message does, it follows the tool message, its text is `PromptLibrary.ImageAttached`, and the
  request the model received carries it.
- **SC-003** `OpenAiCompatibleTransportTests.AUserMessageWithAnImageIsSentAsParts` — the array form, an
  `image_url` part, the `data:image/png;base64,AAAA` URI; the neighbouring test still proves a text-only user
  message is a string.
- **SC-004** `ConversationImageTests` — the `User` overload, `HasImages`, `ToDataUri`, `ToString` printing
  `image/png · 3 bytes` and never the payload, and `ToolResult.Image` holding one entry.
- **SC-005** `ModelCatalogTests.OnlyTheModelsDeclaredToReadImagesSaySo` — Grok's three reading models yes, its mini
  no, DeepSeek and the demo adapter no, `openai-compatible` yes.
- **SC-006** `PromptLibraryTests.TheShippedPromptSendsAPictureThroughViewImage` — rule 9 names the tool and the
  refusal, and survives `SystemPromptFor` with the index and the network both off.
- **SC-007** `TaskToolRegistrationTests` pins the eighteen default names including `view_image`, and
  `McpServerPublishedSetTests` pins `view_image` as registered but not published.
- **SC-008** `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings and the whole suite green —
  1256 passed, 0 failed, 0 skipped, which is the number `README.md` carries.

## Out of scope, on purpose

- **Reading a picture without a model that can.** There is no OCR, no local vision, no captioning. The bytes go to
  the model that was chosen; if it cannot see, the tool says so.
- **Images inside MCP results.** `McpContent.Flatten` still described an `image` as a MIME type and a size, and
  round 4's contract stood unchanged. A browser screenshot was shown by calling `view_image` on the file
  `McpArtefactSink` wrote — one tool call and no new sink. **Closed by `019`**, which attaches the part and keeps the
  sentence, the sink and the path.
- **`fetch_url` returning images**, a `Content-Type: image/*` branch, or a download tool. **Closed by `019`** for the
  four types a provider accepts; every other binary keeps this round's refusal.
- **PDF, audio and video.** The extension table still lies for these: a PDF or a video named as a picture would be a
  provider rejection. SVG is no longer in this list: `SvgImage` renders it to PNG through Svg.Skia, whose SkiaSharp
  binaries ship for Windows, Linux and macOS.
- **Pasted, dragged or piped images**, and an `--image` flag on `run`. A picture enters a conversation the way any
  other evidence does: through a tool, with a path a report can name.
- **Resizing, re-encoding, rotating or downscaling.** The developer's file is shown as it is, or refused with the
  number that says why.
- **Images in session files.** Deliberate (FR-009): a transcript people grep is not a blob store.
- **Deduplication.** Closed by `021` (round 12): a picture the conversation already carries is not attached a second
  time, by any of the three doors, and the answer is a success because the picture is exactly where the model asked for
  it. The first half of this row — a per-turn budget — was closed by `020`: a turn attaches at most 16 MiB of picture,
  counted by the loop, before any of it is re-sent. What neither round does is bound the transcript itself; that is
  T031's remaining clause.
- **An estimate of an image's token cost before the request.** A provider bills pixels it decodes, and the number
  arrives in `Usage` after the call or not at all.

## Stop gates

Work stops and asks if any of these turns out true: **G1** a provider rejects the part shape `OpenAiWire` writes;
**G2** the hand-over needs a change under `Domain/Index` or `Infrastructure/Indexing`; **G3** `ImageContent` needs
a type from outside the BCL, or a provider's name inside `Domain`. None was hit.
