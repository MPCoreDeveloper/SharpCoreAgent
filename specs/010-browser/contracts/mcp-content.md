# Contract — what one `content[]` entry becomes

**Status**: landed 2026-10-30 with `010-browser`. Amends `009-mcp-client`'s FR-009 for image entries; every
other row is what `009` already did and still does.
**Code**: `src/SharpCoreAgent.Infrastructure/Mcp/McpContent.cs` (the table), `McpArtefactSink.cs` (the save
path), `McpTool.cs` (the effects).

This file answers one question for a reader who has never opened this repository: *a server sent me this
entry — what does the model read, and what appears on disk?*

## The rule in one sentence

`text` is passed through; a binary entry is **described**; an image entry is **described and saved** when the
invocation may write and the file fits inside the ceilings, and **described with the reason it was not
saved** when it does not. Nothing in `content[]` is ever dropped, and base64 never reaches the model.

## The table

| `type` | What the server sent | What the model reads |
| --- | --- | --- |
| `text` | `text` | the text, verbatim (`[empty text]` when the string is empty) |
| `image` | `data` + `image/*` `mimeType` | the entry is **saved or described** — see below |
| `image` | `data` + a non-`image/*` `mimeType` | `[image: <mimeType> · <n> base64 characters]` |
| `audio` | `data` + `mimeType` | `[audio: <mimeType> · <n> base64 characters]` — described, never saved |
| `resource` | `resource.text` | `[resource: <uri>]` then a newline and the text |
| `resource` | `resource.blob` + `image/*` `mimeType` | the entry is **saved or described** — see below |
| `resource` | `resource.blob` + any other type | `[resource: <uri> · <mimeType> · <n> base64 characters]` |
| `resource` | no `text` and no `blob` | `[resource: <uri> · <mimeType> · no content]` |
| `resource_link` | `uri`, `name` | `[link: <name> · <uri>]` |
| any other `type` | anything | `[<type>]`, followed by its `text` on a new line when it carried one |
| no `type` | anything | `[an entry with no type]` |
| not an object | a string, a number, a null | `[content that is not an object: <kind>]` |

Entries are joined with one blank line, in the order the server sent them, and the whole answer is bounded at
64 000 characters before the loop's own budget applies. A result with no `content`, or with an empty one,
reads `(the <server> server returned no content)`.

## The saved sentence

A saved image entry replaces its description with a path and a size:

```text
[image: image/png · 48213 base64 characters → .sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png (36.4 KiB)]
```

The same shape applies to a resource blob, which keeps its `[resource: …]` opening:

```text
[resource: file:///shot.png · image/png · 48213 base64 characters → .sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png (36.4 KiB)]
```

The path is workspace-relative, with forward slashes, and is the same string the result's `Created` effect
carries — so a transcript line, a report row and a `read_file` argument name the same file. The size is the
decoded size, formatted by the product's one formatter (`36.4 KiB`, `16 B`).

## The six ways an entry is not saved

A refused entry keeps its description and appends exactly one reason, in this order of checking:

| Reason | When |
| --- | --- |
| `· not saved: this session may not write` | the invocation's `Approval` is `ReadOnly` |
| `· not saved: the call already saved <n> artefacts` | this answer already saved `MaxArtefactsPerCall` files |
| `· not saved: larger than 8 MiB` | the decoded size, or its base64 estimate taken before decoding, exceeds `MaxArtefactBytes` (the sentence names the configured ceiling) |
| `· not saved: the data is not valid base64` | the payload does not decode |
| `· not saved: image/tiff is not a format this client saves` | the MIME type is not one of the types in the table below |
| `· not saved: the workspace refused the write (<reason>)` | the path left the workspace, or the write failed |

None of these makes the call an error: `IsError` stays whatever the server said, and the rest of the answer
is unaffected. A file is written whole (temporary file, then rename) or not at all.

## Names and types

- The name is `<server>-<tool>-<n>.<ext>` under `<workspace>/.sharpcoreagent/artifacts/`, created on demand.
  `<server>` is the connection's prefix exactly as the tool name uses it (`npx`, `npx_2`), `<tool>` is the
  server's tool name folded to `[a-z0-9_]`, and `<n>` counts the saveable entries of that call from one.
- If the name is taken, `<n>` advances until it is free. Nothing is ever overwritten, renamed or deleted.
- The extension comes from the MIME type, and only these are saved: `image/png` → `.png`, `image/jpeg` and
  `image/jpg` → `.jpg`, `image/gif` → `.gif`, `image/webp` → `.webp`, `image/bmp` → `.bmp`. Any other type
  is described with its own name, because a file whose extension lies is worse than a sentence that does not.
- Two ceilings bound one call: `MaxArtefactBytes` (decoded bytes, default 8 MiB, 1 KiB–64 MiB) and
  `MaxArtefactsPerCall` (default 8, 1–64). They are properties of the client, not flags.

## Effects

Every saved entry adds `ToolEffect(ToolEffectKind.Created, "<relative path>")` to the result, in save order,
whatever the tool's `Safety` — including a server tool annotated read-only, which is what a browser's
screenshot tool is. A destructive tool keeps its `CommandRun` effect and gains the `Created` ones after it.
A refused entry adds no effect at all: the turn did not touch the disk.

## What this does not change

`009`'s approval model, `--mcp`'s meaning, `mcp serve`'s published tool set, the `--json` event schema, the
64 000-character ceiling, and the fact that `.sharpcoreagent` is invisible to `list_files`, the scanner and
the index. The transcript carries a path, never bytes: the model cannot see a screenshot, and `read_file`
still refuses binary content.
