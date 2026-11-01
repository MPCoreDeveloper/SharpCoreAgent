# Spec — 019 image ingress

**Status**: landed on `main` as `83c28ea` (2026-10-31) in one commit, with no merge commit — the shape rounds 6 and 9
took. Spec number `019`: `018` is held for `web_search` by a sentence in `specs/000-parity-roadmap.md`, and `014` was
never assigned, so `019` is the next number that claims nothing already spoken for.

**Depends on**: `017` (`ImageContent`, `ToolResult.Images`, `ModelDescriptor.SupportsImages`, the loop's hand-over),
`010` (`McpArtefactSink`, the `Flatten` save delegate), `009` (`McpTool`), `008` (`fetch_url` and the shape of its
refusal).

## Why this round exists

Round 9 gave the model eyes and exactly one way to use them: `view_image` on a path. Two doors still hand it a
*sentence* about a picture instead of the picture:

```
[image: image/png · 36,912 base64 characters] → .sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png (36 KiB)
```

```
the response is image/png, which fetch_url does not read: it returns text and never downloads a binary.
```

Both are the same shortcoming seen twice. In each case the bytes were already in memory, and the round that made a
picture viewable stopped at the seam the bytes arrive through. For MCP there is a workaround — call `view_image` on the
artefact path — and it costs a round trip and a second copy of a file that is already decoded. For `fetch_url` there is
no workaround at all: nothing is written, so the picture is simply gone.

The reason round 9 drew the line where it did is sound and quoted in its own spec: *"a screenshot is shown by calling
`view_image` on the file `McpArtefactSink` wrote — one tool call and no new sink."* That is true and it is not an
answer to *what is in this picture* for a URL. This round keeps the sink and closes the two doors, because the
capability already exists and what is missing is that it is reachable from only one of the three places a picture can
come from.

## What it adds

| Surface | Layer | Shape |
|---|---|---|
| `ImagePayload` | `Infrastructure.Images` | one normalisation table, one size estimate, one ceiling rule — the only place a foreign `image/*` becomes attachable |
| `McpCallResult.Images` | `Infrastructure.Mcp` | the image parts one `tools/call` returned, beside the text that describes them |
| `McpContent.FlattenWithImages` | `Infrastructure.Mcp` | text and pictures from one `content[]`; `Flatten` unchanged for everyone else |
| `McpTool` | `Infrastructure.Mcp` | attaches what qualifies, and says in one line why the rest were not |
| `fetch_url` | `Infrastructure.Tools` | an `image/*` body within the ceilings becomes the picture; every other binary keeps `008`'s refusal |
| `TextLimits.MaxImagesPerResult` | `Infrastructure.Tools` | four pictures per tool result |
| `PromptLibrary.ImagesAttached` | `Application` | the plural sentence for a result that carried more than one |
| the choice between them | `Application.CodingAgent` | by count, on the message the loop already wrote |

## Requirements

- **FR-001** An image is attached only when `ToolInvocationContext.Model` declares `SupportsImages`. A session whose
  model cannot see keeps today's answer byte for byte: the description, the path, and no picture. The gate is read at
  call time from the context, as `view_image` reads it, and the safe default is the same one — no model, no picture.
- **FR-002** Four media types attach, six spellings: `image/png`, `image/jpeg` and `image/jpg` (normalised to
  `image/jpeg`, because that is the only spelling a provider documents), `image/gif`, `image/webp`. Every other type —
  `image/bmp`, which the artefact sink does save, and `image/svg+xml`, `image/tiff`, which neither accepts — is
  described and never attached. The table is a closed list for the reason round 9's extension table is: a provider
  rejects a type it does not carry, and relabelling would only move the failure into the request.
- **FR-003** The ceiling is `TextLimits.MaxImageBytes` (4 MiB), measured from the base64 length before anything is
  decoded: `Length / 4 * 3`, minus the visible `=` padding. That is the estimate `McpArtefactSink` has used since
  round 4; it is extracted rather than copied, so the two ceilings cannot drift apart.
- **FR-004** Nothing is dropped and nothing is rewritten. `McpContent` describes every entry exactly as it does today,
  the sink still writes the file, and the text still names the path — attachment is additive. A resumed session drops
  images (round 9's FR-009), so the sentence and the path remain the whole of what a reloaded transcript keeps.
- **FR-005** One tool result attaches at most `TextLimits.MaxImagesPerResult` (4) images. Each one not attached — over
  the count, over the ceiling, or of a type from FR-002's closed list — is named in one appended line with its reason,
  because a picture that silently failed to arrive invites the model to invent it.
- **FR-006** A session whose model cannot read images is told once, in one line, that the result carried images that
  were not attached and that they must not be described. This is round 9's refusal moved to the door the picture came
  through; `view_image` refuses before opening the file, and this seam has no file to refuse to open.
- **FR-007** `fetch_url` returns an image when all four hold: the media type is one of FR-002's; the body was read
  whole (not stopped at `WebFetchOptions.MaxBytes`, because a truncated PNG is a corrupt picture and base64 of it is a
  worse lie than a refusal); the decoded size is within FR-003's ceiling; and the session's model can see. Otherwise it
  refuses in the shape `008` fixed — the header block, then one sentence that names the numbers — and that sentence no
  longer says `fetch_url` "never downloads a binary", because as of this round it sometimes does.
- **FR-008** One URL, at most one picture: a response body is one body. There is no gallery, no paging and no second
  request.
- **FR-009** The tool's own `Content` still describes what it attached — media type, byte count, the final URL, the
  status and any redirects — and still reports `Read` as its effect, on the final URL. The picture is the answer; the
  sentence is what a person reading the transcript gets, and what a text-only model gets instead.
- **FR-010** No decode, no re-encode, no resize, no sniffing. The bytes are base64'd as they arrived and the claimed
  media type is what the message says: for `fetch_url` the claim is the response's `Content-Type`, for MCP it is the
  part's `mimeType`. A server or a site that mislabels its own payload is not something this round can fix.
- **FR-011** Determinism and the machine's boundaries are unchanged: no new request, no new process, no new file
  written, no flag added, no tool added or removed, and `mcp serve` publishes exactly what it published before
  (`view_image` is still registered and not served).
- **FR-012** The plural hand-over says the same thing the singular one does. Round 9's `ImageAttached` named
  `view_image` as the way back after a reload, which is right for a file and wrong for a URL that was never written;
  the sentence is reworded once, in both forms, to name the rule (call the tool again; `view_image` if the picture is
  a file on disk) rather than one tool's name as if it were the only door.

## Success criteria

- **SC-001** `ImagePayloadTests` — the normalisation table (five accepted spellings, `image/jpg` → `image/jpeg`), the
  closed list refusing `image/bmp`, `image/svg+xml`, `image/tiff` and `null`, the estimate on padded and unpadded
  base64, and the ceiling boundary: at 4 MiB accepted, one byte over refused with the number.
- **SC-002** `McpImageTests` — against the scripted server: a reading model attaches the picture (media type
  normalised, base64 verbatim, the tool message still naming the size), a text-only model attaches nothing and gets
  the FR-006 sentence, an over-ceiling part is named with its number, five parts attach four and name the fifth.
- **SC-003** `FetchUrlImageTests` — a PNG body attached with the header block in its text and `Read` on the final URL;
  `image/jpg` normalised; `image/svg+xml` still refused by name; a text-only session refused with the model named; a
  body stopped at the byte ceiling refused with both numbers; and the neighbouring text tests unchanged.
- **SC-004** `PromptLibraryTests` — `ImagesAttached` is not `ImageAttached`, names no tool that cannot help, and
  both constants still ship in no prompt section of their own.
- **SC-005** `CodingAgentTests` — a result carrying two images produces one user message with `ImagesAttached` and two
  entries; the existing single-image test still expects `ImageAttached`.
## Out of scope, on purpose

- **OCR, local vision, captioning.** Unchanged from round 9: the bytes go to a model that can see, and one that cannot
  is told so.
- **Downscaling, re-encoding, thumbnails.** A picture is shown as it is or refused with the number that says why.
- **PDF, SVG, audio and video.** `fetch_url` keeps `008`'s refusal for every non-image binary; an MCP `audio` part is
  described and never attached; `image/svg+xml` counts as a document, not a photograph.
- **MCP `resource` blobs and `resource_link`s.** A `blob` is a file by another name and the sink already saves the
  image ones; making that path also attach would double the ways one answer becomes a message. It stays a path
  `view_image` can open, which is round 4's contract unchanged.
- **Pasted, dragged or piped images, and an `--image` flag.** A picture still enters through a tool, with a path or a
  URL a report can name.
- **Images in session files, a per-turn budget, deduplication, a token estimate.** Round 9's reasoning stands.
- **`Accept: image/*` negotiation in `fetch_url`.** The request header is unchanged, so a page that content-negotiates
  returns to a text fetch exactly what it returned before this round. A site that only serves images when asked is a
  better `Accept` header, not this slice.
- **Anything served.** `mcp serve`'s published set does not move, and no served tool can attach an image.

## Stop gates

Work stops and asks if any of these turns out true: **G1** an MCP image part has to be decoded to be measured (it does
not — FR-003's estimate is the rule that already refuses a 40 MiB capture before its bytes exist); **G2** the attach
point needs a provider's media-type table inside `Domain` (it does not — `ImagePayload` is `Infrastructure`);
**G3** a provider rejects a `data:` URI carrying `image/gif` or `image/webp` (unverifiable without a live key on this
round's budget, so those two types attach on the declaration their provider documentation makes, and a rejection would
be the finding that moves them out of FR-002's table). None of G1 or G2 was hit.
