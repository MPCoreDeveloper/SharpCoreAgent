# Tasks — 010 `010-browser`: the browser's screenshots become files

Legend: `[X]` done and covered by evidence in this change; `[ ]` not started, kept here so the next pass has
a real list rather than a memory. Paths are as they exist in this repository, which is not always where a
plan written from memory would put them — Phase 0 exists because two of them were wrong.

## Phase 0 — the seams, read rather than assumed

- [X] T001 `McpContent.Flatten` is `internal static string Flatten(JsonElement result, string serverName)`
  (`src/SharpCoreAgent.Infrastructure/Mcp/McpContent.cs:36`) with exactly one call site,
  `McpServerConnection.CallAsync` at line 291. So the overload is a two-file change and no public signature
  has to move (FR-013).
- [X] T002 `McpTool.ExecuteAsync` builds its effects from `Safety` alone — `CommandRun` for a destructive
  tool, `[]` otherwise (`McpTool.cs:89-91`). FR-008 needs the list of files the sink wrote, so the adapter,
  not `McpServerConnection`, is what constructs the sink: the connection has no workspace and must not
  acquire one.
- [X] T003 `PathSafety.TryResolve(Workspace workspace, string path, out string fullPath, out string? error)`
  is `public static` in `src/SharpCoreAgent.Infrastructure/Tools/PathSafety.cs`, used by `WriteFileTool`,
  `ReplaceInFileTool`, `DeleteFileTool` and three readers. It is reachable from `Infrastructure.Mcp` with the
  `using` `McpTool` already carries, so FR-002 needs no new guard — and no second confinement rule.
- [X] T004 `CodeIndexFormat.Size(long)` is `internal static` in
  `src/SharpCoreAgent.Infrastructure/Tools/CodeIndexFormat.cs:113` — the **same assembly** as the sink, so
  the `36.4 KiB` formatter is callable without a new project reference or a public type. `bytes < 1024`
  renders `N B`, which is the only case FR-007's sentence does not show.
- [X] T005 `ToolInvocationContext` is declared in `src/SharpCoreAgent.Domain/Tools/ITool.cs:21`, not in a
  file of its own, and `Approval` defaults to `ApprovalMode.ReadOnly`. `McpToolTests.Context()` is
  `new(Workspace.Create(Path.GetTempPath()))`, so every save test must pass `ApprovalMode.Ask` explicitly
  and the refusal test is the one that keeps the default (FR-010).
- [X] T006 `.gitignore` line 14 is `.sharpcoreagent/`, and `SharpCoreAgent.Infrastructure.csproj` references
  Domain and Application only and already carries `InternalsVisibleTo="SharpCoreAgent.Tests"`. No project
  reference, no `PublicAPI` snapshot (the analyzer covers Domain), and the internal sink is testable (FR-012).

## Phase 1 — the seam, before any file is written

- [X] T007 `src/SharpCoreAgent.Infrastructure/Mcp/McpClientOptions.cs`: `MaxArtefactBytes`
  (`8 * 1024 * 1024`, valid 1 KiB–64 MiB) and `MaxArtefactsPerCall` (`8`, valid 1–64), with the same
  doc-comment shape as the six beside them and two `Require` calls in `Validate()` (FR-006). Done when both
  are properties of the capability rather than flags, and a value outside the range is a construction
  failure like every other ceiling.
- [X] T008 `src/SharpCoreAgent.Infrastructure/Mcp/McpContent.cs`: `internal sealed record McpArtefact(string
  Kind, string MimeType, string Base64, int Ordinal)` and
  `Flatten(JsonElement result, string serverName, Func<McpArtefact, string> save)`. An `image` entry whose
  `mimeType` is `image/*` and a `resource` entry whose `resource` carries a `blob` and an `image/*` type are
  handed to `save`, and its sentence is used verbatim; `text`, `audio`, a non-image `resource`, a
  `resource_link` and an unknown type are described exactly as today. The two-argument overload delegates
  with `save: null`, and a null `save` means "describe, do not save" — so 009's three flattening tests stay
  unedited (FR-001, FR-011).
- [X] T009 `src/SharpCoreAgent.Infrastructure/Mcp/McpServerConnection.cs`: an `internal`
  `CallAsync(string tool, JsonElement arguments, TimeSpan timeout, Func<McpArtefact, string>? save,
  CancellationToken)` overload carrying the body at line 291 as
  `McpContent.Flatten(result, Name, save)`; the existing `public` four-argument overload keeps its signature
  and calls it with `null`. Done when `McpServerConnection` still knows nothing about `Workspace` and
  `McpServerDogfoodTests`' text equality with `SearchCodeTool` is 2/2 without an edit (FR-013).

## Phase 2 — the sink

- [X] T010 `src/SharpCoreAgent.Infrastructure/Mcp/McpArtefactSink.cs` (new, `internal sealed`): takes
  `Workspace`, `ApprovalMode`, the server name, the tool name and `McpClientOptions`; exposes
  `IReadOnlyList<string> Written` (workspace-relative, in save order) and `string Save(McpArtefact)`.
  `Save` is FR-007's refusal ladder in order — approval (FR-010), per-call ceiling (FR-006), estimated size
  `Base64.Length / 4 * 3` *before* decoding (FR-006), `Convert.FromBase64String` (FR-009), measured size
  (FR-006), the five-type MIME table (FR-004), `PathSafety.TryResolve` (FR-002, FR-009) — then decodes,
  names `<fold(server)>-<fold(tool)>-<n>.<ext>` with `<n>` advancing while the file exists (FR-003, FR-005),
  `Directory.CreateDirectory` on demand, writes a temp file in the destination directory and `File.Move`s it,
  and returns FR-007's saved sentence with `CodeIndexFormat.Size`. Done when `Save` has no throw path at all:
  every branch returns a sentence (FR-009).
- [X] T011 `McpTool.ExecuteAsync`: build the sink from `context.Workspace` and `context.Approval`, call the
  internal `CallAsync` overload with `sink.Save`, and build the effects as `CommandRun` (destructive tools
  only, unchanged) followed by one `ToolEffect(ToolEffectKind.Created, relative)` per `sink.Written` entry,
  in save order. `Safety` stays whatever the server annotated. Done when a read-only server tool that
  returned two images reports two `Created` effects and no `CommandRun` (FR-008), and a refused save reports
  neither.

## Phase 3 — the evidence

`tests/SharpCoreAgent.Tests/Infrastructure/McpArtefactTests.cs`, over the existing `ScriptedMcpServer` and a
temp workspace. Each test states the SC it answers.

- [X] T012 `AnImageEntryIsWrittenAndTheTextNamesIt` — one `image/png` entry: exactly one file exists, its
  bytes are the decoded base64, its first eight bytes are the PNG signature, the sentence names the
  workspace-relative path and the size, and `Effects` is one `Created` on a **read-only** tool (SC-001,
  SC-002, FR-008). The context is built with `ApprovalMode.Ask` explicitly.
- [X] T013 `AResourceBlobIsSavedByTheSameRule` — a `resource` entry with `blob` and `mimeType: image/png`
  writes a `.png` with the same sentence shape; a `resource` with `text` is unchanged, in its own test
  `AResourceWithTextIsUnchanged`, so a failure names which of the two shapes broke (FR-001).
- [X] T014 `ASecondCallAdvancesAndNeverOverwrites` — two calls leave `…-1.png` and `…-2.png`, and the first
  file's bytes are byte-identical afterwards (SC-005, FR-005).
- [X] T015 `ReadOnlyApprovalWritesNothingAndSaysSo` — the default `ToolInvocationContext`: no directory, no
  file, the sentence ends `· not saved: this session may not write`, `IsError` is false and `Effects` are
  what a read-only tool reports today (SC-003, FR-010).
- [X] T016 `TheByteCeilingIsCheckedBeforeDecoding` — at `MaxArtefactBytes = 1024`, 1025 bytes are refused with
  `· not saved: larger than 1 KiB` and no file exists, while exactly 1024 bytes are saved (SC-004, FR-006). The
  second half as planned — "the estimate passes and the decoded size does not" — cannot be built: for valid
  base64 the estimate is exact once the trailing `=` padding is subtracted, and whitespace only makes it
  larger, so it fails earlier rather than later. The boundary is the stronger evidence anyway: 1024 and 1025
  bytes both encode to 1368 characters, so a bare `Length / 4 * 3` refused the honest 1 KiB payload and could
  not tell the two sizes apart. `McpArtefactSink.Estimate` subtracts the padding, and this test is what forced
  that change.
- [X] T017 `ThePerCallCeilingSavesTheFirstAndDescribesTheRest` — `MaxArtefactsPerCall: 1` with two images:
  one file, and the second entry carries `· not saved: the call already saved 1 artefacts` (FR-006).
- [X] T018 `AnUnknownMimeTypeIsDescribedNotGuessed` — `image/tiff` writes nothing and says
  `· not saved: image/tiff is not a format this client saves`; the four accepted types each produce the
  right extension in the same test (FR-004).
- [X] T019 `InvalidBase64IsASentenceNotAnException` — `data` that is not base64 writes nothing, names
  `· not saved: the data is not valid base64`, `IsError` stays false, and no temp file is left behind
  (SC-004, FR-009).
- [X] T020 `TheSameAnswerInTheSameStateProducesTheSameName` — two workspaces over the same scripted answer
  produce the same relative path and the same text (FR-014).
- [X] T021 Mutation proof for SC-002, run rather than described: `McpTool`'s `sink.Save` replaced by `null`, then
  only T012 — `failed … Assert.Single() Failure: The collection was empty`. The mutated run stops at the file
  assertion before the sentence, so the plan predicted the wrong assertion but not the wrong outcome; `save:
  null` also empties `sink.Written`, so the `Created` effect disappears with the file. `sink.Save` read back at
  `McpTool.cs:81` and the ten artefact tests ran `[+10/x0/?0]`.
- [X] T022 Confirm the suite passes untouched: no existing assertion edited, and 009's three flattening
  tests in `McpToolTests` are part of the same green run.

## Phase 4 — the surface

- [X] T023 `specs/010-browser/contracts/mcp-content.md`: the flattened `content[]` table with FR-001's two
  saved shapes marked "saved **or** described", FR-007's saved sentence and its six refusals, FR-003's
  naming rule and FR-004's MIME table, FR-008's effects rule, and the two ceilings. Done when a reader who
  has never seen this round can predict what one entry becomes from the table alone (FR-013, SC-007).
- [X] T024 `specs/009-mcp-client/spec.md` FR-009 gains the cross-reference ("amended by 010 for image
  entries — see `specs/010-browser/contracts/mcp-content.md`"), and `specs/009-mcp-client/plan.md`'s
  `image` → `[image: …]` row gains the same one-line pointer. Done when the two specifications agree on the
  page, not only in the code (SC-007).
- [X] T025 `README.md`: the `--mcp "npx -y @playwright/mcp@latest"` command, the artefacts directory, the
  saved sentence shape, and the fact that the file is an ordinary workspace file a later call can open —
  beside the existing MCP section, not in a new one. The suite count is the one the close-out run prints.
- [X] T026 `src/SharpCoreAgent.Cli/HelpText.cs`: `--mcp`'s paragraph names the artefacts directory and says
  a screenshot becomes a file there. No row in the option table, no new option, no new example block
  (SC-006, FR-013).
- [X] T027 `specs/000-parity-roadmap.md`: row 4 and `## Round 4` marked landed with the date, the four-path
  diff of T029 and the numbers of T028 (SC-007).

## Phase 5 — close

- [X] T028 `dotnet build .\SharpCoreAgent.slnx -c Release --no-incremental` → `Build succeeded. 0 Warning(s)
  0 Error(s)`; `dotnet test .\SharpCoreAgent.slnx -c Release` → `total 1200, succeeded 1200, failed 0,
  skipped 0, 12s 786ms`. The delta from 1190 is exactly the ten tests of Phase 3 (SC-006).
- [X] T029 `git diff --stat` over the four paths this round must not touch — `src/SharpCoreAgent.Domain/`,
  `src/SharpCoreAgent.Application/CodingAgent.cs`, `src/SharpCoreAgent.Indexing/`,
  `src/SharpCoreAgent.DependencyInjection/` — **recorded**: 6 files, `+321 −9` (`CodingAgent.cs`,
  `ServiceCollectionExtensions.cs`, `SharpCoreAgent.DependencyInjection.csproj`, `SharpCoreAgentOptions.cs`,
  `Domain/Agents/AgentReport.cs`, `Domain/PublicAPI.Unshipped.txt`), every hunk from a landed-but-uncommitted
  round (`011`'s `TaskTool`, `015`'s `FSharpToolsEnabled`, `012`'s `VerifyGate`) and none this round's.
  The literal zero stayed impossible for the reason the task gave: `HEAD` was `ad9ab36` with 54 uncommitted
  entries when the check ran — 62 once this round's own documentation was written (29 modified, 33 untracked)
  — from rounds `012`, `015`, `011` and this one. It arrived through the commit rather than through an edit:
  rounds 4, 5 and 7 are one commit, `a447a4d`, and `git diff --stat HEAD` over the same four paths is empty.
- [X] T030 The live check, **run and recorded as run**: `--mcp "npx -y @playwright/mcp@latest --browser msedge"`,
  `npx_browser_navigate` on `https://example.com`, `npx_browser_take_screenshot` →
  `<workspace>/.sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png`, 44 912 bytes, PNG signature
  `89 50 4E 47 0D 0A 1A 0A`. `--browser msedge` was passed from the start, because the default channel wants a
  browser Playwright downloads for itself; the default was not tried, so that is a choice this check made rather
  than a finding. Playwright's own copy went to its `--output-dir` (`<server cwd>/.playwright-mcp/`), not into
  the workspace: two files, and only ours is the one the transcript names.

## Deliberately not in this round

- [ ] T031 A browser of our own: no bundled driver, no `--browser` flag, no `browser` tool. The
  decision record stands and `--mcp` is the only door.
- [ ] T032 PDF, video or audio artefacts, and any `application/*` type: the five image types are the whole
  table, and a format whose extension would lie is refused by name (FR-004).
- [ ] T033 Retention, cleanup, deduplication or a size-bounded artefacts directory. Nothing is deleted,
  renamed or collected by this round (FR-005).
- [ ] T034 An `artifacts` tool, a `/files` filter, or index visibility: `list_files` still cannot see
  `.sharpcoreagent` (FR-012).
- [ ] T035 Showing the image to the model as an image. `ToolResult` carries text; a screenshot the model
  could *see* is a content-block change in `Domain.Conversation`, which is its own round.

## Open, and why

- **The model cannot see the screenshot.** It reads a path and a size, then `read_file` refuses binary
  content. Making the bytes visible is a `ToolResult` content model, not an artefact sink. The live check of
  2026-10-30 showed the cost of this: the run could not confirm its own artefact and spent three `run_shell`
  calls listing directories to find it.
- **A browser server keeps a second copy.** Playwright writes its own file under its `--output-dir`
  (`.playwright-mcp/` beneath the *server's* cwd, resolved against its allowed roots and not against the
  workspace), so the same screenshot exists twice and the two can be in different trees. Ours is the one the
  transcript names; redirecting the server's copy is that server's configuration, not something a client can
  do for it.
- **No way to list what was captured.** `/files` shows `Created` effects for the turn that made them; a
  session-long catalogue would need a workspace query nothing has today.
- **Artefacts are per workspace, not per session.** Two chats in one workspace share
  `.sharpcoreagent/artifacts/`, and FR-005's numbering means the second chat's first screenshot is `-2`.
  That is deterministic and is what the spec asks for; naming by session would make the path unpredictable
  from the transcript.
- **A refused save is only as visible as the sentence.** There is no counter, no log line and no warning
  colour: if a developer does not read the tool result, they do not learn the file was not written.

