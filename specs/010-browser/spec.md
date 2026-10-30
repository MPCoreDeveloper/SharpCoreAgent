# 010 — The page a browser returns: a screenshot is a file, not a sentence

**Status**: implemented 2026-10-30 — `T001`–`T030`, both phases (round 4 of
[`000-parity-roadmap.md`](../000-parity-roadmap.md)). `McpArtefactSink` writes an `image` entry or an image
`resource` blob to `<workspace>/.sharpcoreagent/artifacts/`, the answer names the path and the size or one
`· not saved:` sentence, and a saved entry adds a `Created` effect even for a tool the server annotated
read-only. Ten tests in `McpArtefactTests`; the whole suite is 1200/1200 in `Release` at 0 warnings. Measured
live against `npx -y @playwright/mcp@latest`: 44 912 bytes of `https://example.com` at
`.sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png`. T031–T035 stay open, listed in `tasks.md`, and the
measurements are in `plan.md`'s `## As built`.
**Depends on**: `009-mcp-client` (the client, `McpContent`, `McpClientOptions`), `001-coding-agent-core`
(`ToolResult`, `ToolEffect`, the approval the loop decides before a tool runs), `003-terminal-console-agent`
(the report that lists a turn's effects)

## Why this feature exists

Round 3 built the door and a browser server already walks through it: `--mcp "npx -y
@playwright/mcp@latest"` drives a real browser today, and a turn that navigates and reads
`browser_snapshot` gets the page's accessibility tree as text. Three things are still missing, and all
three are the same missing thing seen from three sides.

**The bytes are thrown away.** `McpContent.Binary` describes an `image` entry as its MIME type and its
base64 length — `[image: image/png · 48213 base64 characters]` — and then drops the 48 213 characters.
That sentence is honest and it is the end of the road: a screenshot a model cannot see and a developer
cannot open is a fact about a page nobody has.

**The turn does not say it wrote anything.** `browser_take_screenshot` is annotated `Read-only: **true**`
in the server's own README (measured 2026-10-30), so `McpTool.Safety` is `ToolSafety.ReadOnly` and the
call runs without a prompt — and `McpTool.ExecuteAsync` reports a `ToolEffect` only in its
`ToolSafety.Destructive` branch, so a turn that produced an image reports that it changed nothing. Adding
a file to the workspace is exactly what `ToolEffectKind.Created` exists for.

**The file the server does write is unreachable.** By its README the server saves its own copy: "If not
specified, the screenshot is saved into the output directory as `page-{timestamp}.{png|jpeg|webp}`". That
directory is the server's to name. Our read tools resolve against the workspace and the roots attached to
it (round 1), so a screenshot in a temp directory is real, correct, and invisible to the next tool call;
and the model has no path it can hand back that anything in this process can open.

**What round 4 adds** is therefore small and singular: take the bytes, bound them, write them once under
`<workspace>/.sharpcoreagent/artifacts/<server>-<tool>-<n>.<ext>`, and answer with the path and the size
beside the description the client already returns — plus the `Created` effect that says the turn left a
file behind. When it cannot write, it keeps the sentence it writes today. No flag, no tool, no
dependency, no second protocol client, and no bundled browser: the browser is the developer's, installed
by them, named by them on `--mcp`.

**This amends one sentence of `009`.** Its US4 and FR-009 say a `content[]` entry this client does not
understand "is *described* rather than dropped or dumped as base64". That remains true for everything
except an image, which is now described **and saved**. Nothing else in `009` moves: the 64 000-character
flattening ceiling, the blank-line joining, the approval model and the `--mcp` surface are untouched. An
`audio` entry, a non-image `resource` and a `resource_link` are described exactly as they are today.

## User stories

### US1 — The screenshot is a file (P0, Phase 1)

As a developer who asked an agent to look at a page, I want the screenshot to exist on disk at a path the
transcript names, so that "I checked the layout" is something I can verify with my own eyes.

Acceptance:

- A `tools/call` whose result carries one `image` entry with `mimeType: image/png` writes exactly one
  file, and the bytes are the decoded base64 the server sent — a PNG that opens.
- The file is `<workspace>/.sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png` for a call to that
  tool on that server: the prefix is the server's folded name — `npx`, because the name comes from the
  command's executable stem (`McpLauncher.Stem`), not from the package it launches — the tool half is the
  server's tool name folded by `McpToolName.Fold`, and the ordinal starts at one.
- The model's text carries the path and the size, beside the description it already gets:
  `[image: image/png · 48213 base64 characters → .sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png (36.4 KiB)]`
- The result is not an error, and the sentence contains no base64: the transcript gets a path, not a
  payload.

### US2 — A turn that may not write still answers (P0, Phase 1)

As a developer running with `--approval readonly`, or reading a sub-agent's transcript, I want a
screenshot to degrade to the sentence rather than fail the call, so that a read-only turn is still a turn
and not an error with a browser attached.

Acceptance:

- With `ApprovalMode.ReadOnly` in the invocation context, no file is created and no directory is created.
- The text keeps today's description and names the reason:
  `[image: image/png · 48213 base64 characters · not saved: this session may not write]`.
- The result is not an error: the server's `isError` is what decides that, and it was false.
- The same holds when the write itself fails — a full disk, a read-only directory, a path that disappears
  between checks — the reason changes, the shape does not, and nothing is half-written.

### US3 — The report says what the turn left behind (P1, Phase 1)

As the person reading the turn report, I want a saved screenshot listed like any other created file, so
that "nothing was changed" is never printed by a turn that changed the workspace.

Acceptance:

- A saved artefact adds `ToolEffect(ToolEffectKind.Created, "<relative path>")` to the result, even though
  the tool is annotated read-only — the write is the client's, and the client reports it.
- Several artefacts in one answer produce several effects, in the order the entries appeared.
- The `CommandRun` effect a destructive server tool reports is unchanged, and a call that saves nothing
  reports exactly the effects it reports today (none, for a read-only tool).
- The console's report and the transcript show the artefact by its workspace-relative path, because that
  is the only path anything in the session can open.

### US4 — The ceiling belongs to the client (P1, Phase 1)

As a developer whose browser server just answered with a 40 MiB page capture, I want the client's ceiling
to win, so that one call cannot fill the disk or the transcript.

Acceptance:

- A decoded image larger than `MaxArtefactBytes` is not written; the text names the ceiling it exceeded.
- More than `MaxArtefactsPerCall` images in one answer: the first n are written, the rest are described
  with the reason.
- A `mimeType` this client does not save, and data that is not valid base64, are described with a reason
  naming the MIME type or the defect — never a truncated file, never a guess at an extension.
- No failure mode of the save path turns into a tool error, and none of them throws.

### US5 — Two screenshots are two files (P1, Phase 1)

As a developer walking an agent through three pages, I want the third screenshot to still be there when I
open the first, so that the run's evidence survives the run.

Acceptance:

- A second call that would produce the same name advances the ordinal instead of overwriting: `…-1.png`
  then `…-2.png`, and the first file's bytes are untouched.
- Names depend only on the server, the tool, the entry's position and what is already on disk — two runs
  over the same responses produce the same names in the same order.

### US6 — The developer is told which browser to hand over (P2, Phase 2)

As a developer who has never run an MCP server, I want the command line written down, so that round 4 is
one copy-paste and not an afternoon of reading somebody else's README.

Acceptance:

- `README.md` gains the section, with `--mcp "npx -y @playwright/mcp@latest"` written out, the tools it
  adds, and where the artefacts land.
- `HelpText`'s `--mcp` paragraph gains the sentence that says a server's screenshots are saved under
  `.sharpcoreagent/artifacts/`.
- The documentation says plainly that the browser is the developer's to install, and names the servers
  known to work with this client, with the date the claim was measured.

## Functional requirements

- **FR-001** Two shapes are saved: an entry with `type: "image"`, a string `data` and an `image/*`
  `mimeType`; and a `resource` entry whose `resource` carries a string `blob` and an `image/*` `mimeType`.
  Everything else in `content[]` — `text`, `audio`, a non-image `resource`, a `resource_link`, a type this
  release does not know — is described exactly as `McpContent` describes it today.
- **FR-002** Artefacts go to `<workspace root>/.sharpcoreagent/artifacts/`, created on demand, resolved
  through the invocation's `Workspace`, and never written outside its root. The roadmap's "session
  directory" is realised as this path: `--session` may point anywhere, and a workspace-relative path is
  the only one an effect, a report or a later tool call can open.
- **FR-003** The name is `<server>-<tool>-<n>.<ext>`: `<server>` the connection's name exactly as the tool
  name uses it (`McpToolName.ForServer(McpLauncher.Stem(command), ordinal)` — `npx`, or `npx_2` for a second
  launcher of the same stem), `<tool>` the server's tool name folded by `McpToolName.Fold` (so `[a-z0-9_]`
  survives and everything else becomes an underscore), `<n>` the entry's position among the artefacts of
  that call, starting at one. The file name mirrors the tool name the model calls, so a transcript line and
  a file on disk can be matched by eye — including the consequence that two `npx` servers are `npx…` and
  `npx_2…`, because that is what the model sees too.
- **FR-004** The extension comes from the MIME type and only these are saved: `image/png` → `.png`,
  `image/jpeg` and `image/jpg` → `.jpg`, `image/gif` → `.gif`, `image/webp` → `.webp`, `image/bmp` →
  `.bmp`. Any other `image/*` type is described with its type and not saved, because a file whose
  extension lies is worse than a sentence that tells the truth.
- **FR-005** An existing file is never overwritten: if the name is taken, `<n>` advances until it is free.
  Nothing is deleted, renamed or garbage-collected by this round.
- **FR-006** The ceilings are properties of the capability, on `McpClientOptions` beside the others and
  validated like them: `MaxArtefactBytes` (decoded bytes, default 8 MiB, 1 KiB–64 MiB) and
  `MaxArtefactsPerCall` (default 8, 1–64). Neither is a flag, for the reason the fetch ceilings are not.
- **FR-007** A saved entry reads
  `[image: image/png · 48213 base64 characters → .sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png (36.4 KiB)]`,
  and an entry that was not saved keeps the description and names one reason:
  `· not saved: this session may not write` · `· not saved: larger than 8 MiB` · `· not saved: the call
  already saved 8 artefacts` · `· not saved: image/tiff is not a format this client saves` · `· not saved:
  the data is not valid base64` · `· not saved: the workspace refused the write (<reason>)`. Byte counts
  are written the way every other byte count in this product is (`36.4 KiB`, the `0.0 KiB` formatter).
- **FR-008** Every saved entry adds `ToolEffect(ToolEffectKind.Created, "<relative path>")` to the result,
  whatever the tool's `Safety` — including a server tool annotated read-only, which reports no effect
  today. Effects keep content order, and the `CommandRun` effect of a destructive tool is unchanged.
- **FR-009** None of this can fail the call. A ceiling, a MIME type, invalid base64, a missing directory, a
  refused or failed write: each is a sentence in the text, never an exception, never `IsError` unless the
  server set it, never a partially written file. A file is written whole or not at all.
- **FR-010** The write happens only when the invocation context's `Approval` permits writing
  (`ApprovalMode.Ask` or `ApprovalMode.AllowAll`); under `ReadOnly` the entry degrades. The approval decision
  itself does not change — the model chooses no path, the directory is fixed and the name is derived, so
  there is nothing to steer and no second prompt to ask.
- **FR-011** Nothing new reaches the model or the wire: `ToolResult.Content` carries the sentence and never
  base64, and the `--json` event schema is unchanged because the effects it already carries now name the
  artefact.
- **FR-012** `.sharpcoreagent` stays invisible to the workspace tools and the index exactly as it is today
  (`IgnoredPaths`, `WorkspaceFileScanner`, `IndexPaths`): artefacts are never indexed, searched or listed
  by `list_files`. `.gitignore` already excludes `.sharpcoreagent/`, and it stays excluded.
- **FR-013** No new flag, no new tool name, no new dependency, no change to `--mcp`'s meaning or to
  `mcp serve`'s published set. The one new document is `contracts/mcp-content.md`, which states the
  amended flattening rule.
- **FR-014** Determinism: the same responses in the same directory state produce the same file names, the
  same bytes and the same text, on every run.

## Key entities

- **Artefact**: one binary entry a server returned, made durable. It has a server, a tool, an ordinal, a
  MIME type, an extension, the decoded bytes and a workspace-relative path. It is not a `ToolResult` field:
  it is a file plus a sentence plus an effect.

## Non-goals (this round)

- **A browser we ship.** No bundled browser, no driver manager, no JavaScript engine of ours, no `browser`
  tool of ours, no `--browser` flag — the decision record of 2026-10-28 stands, and the server's own
  `browser_navigate` (published to the model as `npx_browser_navigate`) is the browser tool, with the
  schema its authors maintain.
- **Images in the transcript.** Nothing adds an `image` content part to a `ChatMessage`, sends base64 to a
  model, or asks a provider to look at anything. The bytes go to disk; the model gets a path.
- **Looking at the picture.** No OCR, no captioning, no "describe this screenshot" fallback: the developer
  looks, and the model reads the accessibility tree it already reads.
- **Anything that is not an image file.** Video, audio, PDFs, HAR files, Playwright traces: described, not
  saved. A trace arrives as a link or a blob and this round does not become a download manager.
- **Artefact lifetime.** No cleanup at session end, no retention policy, no `--artifacts` setting, no
  `artifacts` subcommand, no `undo`. The directory grows; that is recorded, not hidden.
- **The server's own copies.** Its `--output-dir` is its business: we neither adopt, move nor delete what
  it wrote. Ours is the copy the transcript names.
- **Re-encoding.** Bytes in, bytes out. No resizing, no thumbnailing, no format conversion, no metadata
  stripping.

## Success criteria

- **SC-001** A scripted `tools/call` answer carrying one `image/png` writes exactly one file: its bytes are
  the decoded base64, its first eight bytes are the PNG signature, and the model's text names it
  workspace-relative with its size. Asserted by a test that reads the file back.
- **SC-002** The requirement is load-bearing: delete the save path and SC-001's test fails, because the
  text reverts to `[image: image/png · N base64 characters]` and no `Created` effect exists.
- **SC-003** Under `ApprovalMode.ReadOnly` no directory and no file are created, the sentence ends
  `· not saved: this session may not write`, `IsError` is false, and `Effects` are what a read-only tool
  reports today.
- **SC-004** Every ceiling and defect is a sentence, not an error: one byte over `MaxArtefactBytes`, a ninth
  image in one call, `image/tiff`, and data that is not base64 each produce no file, a named reason and
  `IsError == false`.
- **SC-005** Two calls to the same server tool leave `…-1.png` and `…-2.png`, and the first file's bytes are
  unchanged — no artefact is ever overwritten.
- **SC-006** The surface does not move: the tool registry gains no name, `HelpText`'s option table gains no
  row, `--mcp` and `mcp serve` behave identically for a workspace with no artefacts, and
  `dotnet build -c Release` is zero-warning with the suite green and the new count in `README.md`.
- **SC-007** The change is findable: `contracts/mcp-content.md` states the amended rule, `009`'s FR-009
  carries the cross-reference, `README.md` and `HelpText` name the directory, and the roadmap's Round 4
  paragraph records the four-path diff and the measurements the round actually printed.

## Assumptions and risks

- **The annotation is today's, not tomorrow's.** `browser_take_screenshot` is `readOnly: true` in its README
  as measured on 2026-10-30. If a server annotates its screenshot tool destructive instead, nothing in this
  spec changes except that the call is prompted and reports a `CommandRun` effect beside the `Created` one.
- **Two copies may exist.** The server's `--output-dir` copy and ours can both be written for one call. Our
  copy is the one the transcript names, and the documentation says so rather than pretending the other does
  not exist.
- **A screenshot can contain a secret.** It is written under an ignored path, it is never uploaded and it is
  never read back into a model's context, which is the same rule every file the agent writes lives under
  (VIII). It is not encrypted, and this spec does not claim otherwise.
- **A hostile server is bounded.** `MaxArtefactBytes` × `MaxArtefactsPerCall` is the most one answer can
  cost, chosen so a server cannot fill a disk in one call; the numbers are validated like every other
  ceiling in `McpClientOptions`.
- **`009`'s contract changes under a landed spec.** That is exactly why the amendment is written in three
  places — this spec, the new contract file, and `009`'s FR-009 — rather than only here.
- **A sub-agent's screenshot is a sentence unless the child may write.** `task` (round 5) runs its child at
  `ApprovalMode.ReadOnly` unless the conversation already allows everything, and the artefact write obeys
  the same rule as every other write (FR-010). Under the default `--approval ask` a child can navigate and
  read a page but leaves no PNG behind; `--approval allow` (or `-y`) is what makes the child's screenshots
  land. That is the answer `task` already gives for `write_file`, and it is written down here so that
  nobody has to discover it from a missing file.



