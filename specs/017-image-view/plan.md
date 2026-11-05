# Plan — 017 image view

## Shape

```text
view_image { "path": "shots/shot.png" }
        |
        v
ViewImageTool   PathSafety -> extension -> size -> context.Model.SupportsImages -> File.ReadAllBytes
        |
        v
ToolResult.Image(content: "'shots/shot.png' (image/png · 36912 bytes) is attached to the next message.",
                 image:   ImageContent { MediaType = "image/png", Base64 = "…" })
        |
        v
CodingAgent     ChatMessage.Tool(result)                      <- text only, as every provider requires
                ChatMessage.User(PromptLibrary.ImageAttached, [image])   <- the carrier
        |
        v
OpenAiWire      [{ "type": "text" }, { "type": "image_url", "image_url": { "url": "data:image/png;base64,…" } }]
```

## Layers, and what each one is allowed to know

**Domain (`Conversation/ImageContent.cs`, `ChatMessage`, `ToolResult`, `ModelDescriptor`).** An image is bytes and
the name of their format, and both are in the transcript, so they are Domain types. `ImageContent` holds base64
rather than `byte[]` (one encoding, at the point of reading, instead of a second copy in the transport) and
carries no path (a path means nothing to a provider, and the file can change between the call and the request).
`ToDataUri()` is RFC 2397, not a vendor's field name — the vendor's field is written in `Infrastructure`, which is
the rule this round had to keep: no provider name appears in `Domain`. `ModelDescriptor.SupportsImages` joins the
other declared capabilities, `false` by default, for the reason the type already documents: a capability that is
declared can be refused before a request is spent, and a model asked to describe a picture it never received will
invent one.

**Application (`CodingAgent`, `PromptLibrary`).** One `if` in the loop and two sentences in the prompt. The loop
does not know what `view_image` is: it attaches whatever a `ToolResult` carries, which is why a second picture-
producing tool would need no loop change. The sentences are prompt text and belong where the product's prompts
live — operating rule 9 for the guidance, `ImageAttached` for the words a reloaded transcript keeps.

**Infrastructure (`Tools/ViewImageTool.cs`, `Tools/TextLimits.cs`, `Chat/OpenAiContent`, `Chat/OpenAiWire`,
`Providers/*`).** The tool is the only new door, and it is a read: `PathSafety.TryResolveRead` (so an attached
root works and a write path does not), the extension table, the ceiling, the model gate, then one
`File.ReadAllBytesAsync`. The provider half is `OpenAiContent.ImageDataUris` plus a converter that writes a string
when there are none — the shape every request sent before this round already had — and an array of parts when
there are. The catalogues gain the flag: Grok's reading models, DeepSeek's none, and `openai-compatible` yes for
every id, because that provider is the developer's own endpoint and the flag is the only thing that can tell the
tool to try.

**DependencyInjection and CLI.** `AddTools` registers `view_image` beside the other reads, which makes eighteen
default tools; `ModelsCommand` prints `images=yes|no`, so the flag the tool consults is visible to the developer
who has to choose a model. No flag adds the tool and none removes it, which is what lets rule 9 be unconditional.

## Why the picture rides on a user message

Every OpenAI-compatible provider accepts `image_url` parts on the **user** role and none accepts them on the
**tool** role: a `content[]` array on a tool message is not part of the shape these endpoints read, so an image
sent there is a rejected request rather than a degraded answer. So the tool's result keeps its sentence and the
loop hands the bytes over in the message that follows it. Two consequences are in the contract, not discovered
later: the transcript order is call → sentence → picture, and a session file that drops images still leaves a
model with the sentence that tells it to look again.

## Complexity Tracking

| Choice | Why it is not the simpler thing | Cost |
|---|---|---|
| An image on `Domain.Conversation`, not on the wire | A transport-only image would have to be invented again by every host and would never appear in a report, a session or a test that does not speak HTTP | `PublicAPI` grows two dozen lines in `Domain`, and the session document has to say why it does not carry them |
| Base64 `string` rather than `byte[]` | Every wire format asks for base64; storing bytes means encoding in the transport, on every request, forever | `ByteCount` is derived from the string's length, so it is approximate for the last 3-byte group — asserted as approximate, not as the file's size |
| The bytes, not the path | A path is worth nothing to a provider, and the file can change between the tool call and the request that carries it | A 4 MiB file is 5.6 MB in the transcript; the ceiling is what keeps that a number rather than a surprise |
| The capability gate inside `view_image`, not in DI | The model a turn runs on changes per turn (`/model`, and round 8's roles), so a registry built at startup cannot be the place that decides | A tool that is registered and refuses looks like a working capability until it is called; rule 9's second half exists to make the refusal useful |
| A refusal is a failed `ToolResult`, not an exception | The model has to read it and change behaviour; an exception would end the turn instead of redirecting it | The refusal text carries instructions ("do not describe", "shrink it"), which is prompt text living in a tool |
| Session files drop images | A transcript people open and grep is not a blob store, and it is written on every turn | A resumed conversation must re-call `view_image`; the `ImageAttached` sentence is what makes that discoverable rather than a silent loss |
| A new tool rather than a `read_file` mode | `read_file`'s answer is a numbered listing, and its contract says so; a `--binary` flag would change the meaning of 2 000 lines | One more name in the catalogue and one more rule in the prompt |
| 4 MiB (`TextLimits.MaxImageBytes`) rather than 20 | 4 MiB is a full-page screenshot, a diagram or a photograph at a readable scale; 20 MiB would be a request body no provider accepts | An oversized file is refused with both numbers instead of being downscaled behind the developer's back |

## Verification

- `ViewImageToolTests` (eleven test methods) — the hand-over, the media-type theory, and every refusal the tool
  owns: text-only model, no model, wrong extension, empty, over the ceiling, missing, directory, outside the
  workspace, and an attached root naming its own root.
- `CodingAgentTests.AnImageAToolReturnsArrivesOnTheNextUserMessage` — positions, `HasImages` on both messages, and
  the image present in the second request the scripted model received.
- `OpenAiCompatibleTransportTests.AUserMessageWithAnImageIsSentAsParts`, beside the existing string-form test.
- `ConversationImageTests` (five) — the Domain surface, including `ToString` never printing the payload.
- `ModelCatalogTests.OnlyTheModelsDeclaredToReadImagesSaySo`, `PromptLibraryTests.TheShippedPromptSendsAPictureThroughViewImage`,
  `TaskToolRegistrationTests` (eighteen names), `McpServerPublishedSetTests` (`view_image` not published).
- One test moved for an honest reason: `ChatLoopTests.NearingTheCeilingSaysSoBeforeTheSessionStops` raised its
  ceiling from 1 700 to 2 000 tokens, because the demo adapter bills characters and the prompt grew rule 9.

## As built

Four things a reader of the spec above would otherwise have to rediscover:

1. **`ByteCount` is derived, not measured.** It is `Base64.Length / 4 * 3`, so a test that asserted the file's raw
   length would be asserting arithmetic that padding makes wrong. The test asserts the rendering
   (`image/png · 3 bytes`) and the fact that the payload is absent, which is what the type promises.
2. **The refusal order is the safety property.** Extension, then size, then model, then read: a text-only session
   is told it cannot see a picture it also could not have read, and the file handle is opened only after both the
   size and the capability have passed. Moving the model check earlier would have been simpler and would have told
   a text-only model about a file that does not exist.
3. **The prompt grew and the ceiling followed.** `ImageAttached` and rule 9 add characters to every request, and
   the demo adapter's token count is characters — the only test that noticed was the loop's ceiling test, which is
   exactly the test that exists to notice.
4. **Nothing in `mcp serve` moved.** The published set is a compile-time allow-list of five read-only index tools,
   so `view_image` is reachable from a normal session and not from a served one; the test that pins it says so in
   one `InlineData`, and a later round that wants to serve images has to change that line on purpose. **T033 did**:
   the allow-list now holds six names and `AnswerBody` writes an `image` part.

## SVG amendment (T027)

An SVG cannot travel as itself: every provider in the catalogue accepts PNG, JPEG, GIF and WebP, and an
`image/svg+xml` part is a rejected request. `SvgImage` therefore renders through `Svg.Skia` at the document's own
`width`/`height` or `viewBox`, disables JavaScript before parsing, and caps either side at 4 096 pixels so one
enormous artboard cannot allocate a gigabyte. The PNG then goes through the same `ImagePayload` ceiling, duplicate
check and turn budget as a native picture, and the tool's sentence names both the source size and the rendering.

The renderer is in `Infrastructure`, not `Domain`, and the SkiaSharp native assets are referenced for Windows,
Linux and macOS. `--image` uses the same `ImageFile` path, so a workspace SVG enters a `run` prompt the same way a
PNG does; stdin still recognises only the four byte signatures, because an extensionless stream would need an XML
sniff and that is a different door.

## PDF amendment (T027b)

`view_image` accepts `.pdf`. `PdfImage` opens the bytes with PdfPig, adds its Skia page factory and renders one page —
page 1 by default, `page` selects another — to PNG. The page keeps its own dimensions at scale 1 and is reduced only
when either side would exceed 4096 pixels; the PNG then goes through the same 4 MiB, duplicate and budget rules as
every other picture. There is no PDFium or Ghostscript child process.

`--image` uses the same path for page 1. Audio and video remain refused: extracting frames or audio needs a separate
runtime and a contract of its own.

## Resize and rotate amendment (T028)

`view_image` now takes an optional `max_dimension` (64–4096) and an optional `rotation` (90/180/270). They are requests,
not a policy: without them the bytes are the file's own; with them `ImageResize` and `ImageRotate` decode through
SkiaSharp, apply the downscale and then the clockwise turn, re-encode PNG and name the rendered size in the answer. A
picture that already fits is returned untouched when no transformation is asked for. An undecodable file or a number
outside the range is a failed `ToolResult`. Arbitrary rotation angles are refused; only quarter turns are supported.

