# Contract: the `view_image` tool, and the message that carries a picture

**Feature**: `017-image-view` — see [../spec.md](../spec.md) and [../plan.md](../plan.md)

This is the whole surface a model sees, the whole behaviour a caller may rely on, and the wire shape that carries
the result. Anything not stated here is not implemented.

## Schema advertised to the model

```json
{"type":"object","properties":{"path":{"type":"string","description":"Image path relative to the workspace root."}},"required":["path"],"additionalProperties":false}
```

| Argument | Required | Rule | Refusal |
|---|---|---|---|
| `path` | yes | non-empty string, resolved through `PathSafety.TryResolveRead` — the workspace root or a read-only attached root | a missing argument or malformed JSON is `ToolArgumentException` from `ToolArgumentReader`; a path outside every root is `ToolResult.Refused` |

Safety is `ToolSafety.ReadOnly`, so a plan turn and a `readonly` session call it and no approval prompt appears.
The tool is registered unconditionally — no flag adds it and none removes it.

## Result

Success is the file's identity, workspace-relative, and the image itself:

```text
'shots/shot.png' (image/png · 36912 bytes) is attached to the next message.
```

- `ToolResult.Images` holds exactly one `ImageContent`, whose `Base64` is the file's bytes and whose `MediaType` is
  what the extension named.
- `ToolResult.Effects` holds one `ToolEffect(ToolEffectKind.Read, "<relative path>")`; an attached root's file is
  named relative to that root, as `007` requires.
- The content is never truncated: the text is one sentence, and the picture is not text.

Failure is a `ToolResult` with `IsError` set, no images, and one sentence:

| Case | Content |
|---|---|
| a directory | `'<path>' is a directory. Use list_directory to see what it contains.` |
| no such file | `no file at '<path>'` |
| an extension outside the table | `'<path>' is not an image this tool can show. It reads files named .png, .jpg, .jpeg, .gif or .webp; use read_file for text.` |
| a zero-byte file | `'<path>' is empty (0 bytes), so there is nothing to show.` |
| over the ceiling | `'<path>' is <n> bytes; view_image shows at most 4194304 bytes (4 MiB). Ask the developer to shrink it, or to save a smaller copy.` |
| a model declared text-only | `'<path>' was not opened: <provider:id> is declared text-only in this build and cannot read images. Do not describe what the image contains — say that you cannot see it, and let the developer switch to a model that reads images.` |
| no model in the context | the same sentence with `the session's model` in place of the name |
| an unreadable file | `'<path>' could not be read: <reason>` |

The order is part of the contract: resolve, directory, existence, extension, size, capability, read. A refused call
never opens the file, and a text-only refusal carries no effect, so a turn that could not see has nothing to report
as read.

## The carrier message

A tool result never travels to a provider as an image. The loop appends, after the tool message:

```text
role: user
content: PromptLibrary.ImageAttached
images: [ <the ImageContent from ToolResult.Images> ]
```

`PromptLibrary.ImageAttached` reads: *"Here is the image a tool just returned, attached to this message. If the
transcript was reloaded from disk, the sentence survived and the image did not: call view_image again rather than
describing what you remember."*

Three properties follow, and each is asserted rather than described:

1. **Order** — the carrier comes after the tool message that produced it, in the same turn.
2. **One carrier per result** — two picture-producing calls in one round produce two carriers, each after its own
   result; the images never share a message.
3. **Any tool, not this tool** — the loop reads `ToolResult.Images`, so a future tool that answers with a picture
   needs no loop change. `view_image` is simply the first.

## The wire

A user message with no images is serialised exactly as before: `"content": "<text>"`.

A user message with images is serialised as an array of parts, text first:

```json
{
  "role": "user",
  "content": [
    { "type": "text", "text": "Here is the image a tool just returned, …" },
    { "type": "image_url", "image_url": { "url": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==" } }
  ]
}
```

`ImageContent.ToDataUri()` produces the URL. Base64 is written without line breaks, and `ByteCount` is derived from
its length (`Base64.Length / 4 * 3`), so it is the pre-encoding size rounded down to the last whole group.

## Which models are declared to read an image

| Provider | Declared `SupportsImages` |
|---|---|
| `grok` | `grok-4.7`, `grok-4`, `grok-3` yes; `grok-3-mini` no |
| `deepseek` | none |
| `demo` | none — it is a scripted adapter and reads no bytes |
| `openai-compatible` | every id yes, from `DescriptorFor`, because the endpoint is the developer's own |

`models` prints `images=yes` or `images=no` beside `tools=yes|no`, so the flag the tool consults is visible before a
turn is spent. The flag is declared, never probed: no request is made to discover it.

## Invariants

1. **Read-only.** `view_image` is `ToolSafety.ReadOnly`, and its only effect is `Read`. It writes nothing, starts
   nothing and reaches no network.
2. **No flag.** Registered by `AddTools` in every build, so rule 9 of the prompt always has its tool.
3. **One ceiling, one table.** The five extensions and `TextLimits.MaxImageBytes` are the whole of what the tool
   accepts; both refusals name their own numbers.
4. **The capability gate is inside the tool.** The model is read from `ToolInvocationContext.Model` at call time,
   because a session can change models between turns.
5. **No payload outside the transcript.** `ImageContent.ToString()` prints the media type and the size; a log line, a
   tool summary and a session file never carry base64.
6. **Session files keep the sentence.** A reloaded transcript has the carrier's text and no images, and the text
   says what to do about it.
