# Plan — 010 Browser

## Shape

One sink, one directory, no new flag.

```text
tools/call answer
      |
      v
McpServerConnection.CallAsync(tool, args, timeout, save, ct)      <-- internal overload
      |
      v
McpContent.Flatten(result, serverName, save)                      <-- internal, one call site today
      |            |
      |            +-- image entry ---------> save(McpArtefact) --> McpArtefactSink
      |            +-- resource blob -------> save(McpArtefact) --> McpArtefactSink
      |                                                             |  approval? ceiling? mime?
      |                                                             |  decode, name, write
      |                                                             v
      |                                                     <workspace>/.sharpcoreagent/artifacts/<server>-<tool>-<n>.<ext>
      v
  text: "[image: image/png · 48213 base64 characters → .sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png (36.4 KiB)]"
      |
      v
McpTool.ExecuteAsync --> ToolResult with CommandRun (destructive) + Created (per file)
```

Nothing else learns that a file exists: no new `ITool`, no new flag, no change to the loop, the approval
model, `McpCallResult`, or the system prompt. The capability arrives through the tool that was already
registered, which is the same reason round 4 needed no browser of our own.

## Why the sink is called during flattening, not after

The transcript must name the file the call just wrote, and the name is only known once the file is chosen.
So the substitution happens *inside* the flattening loop, through a delegate:

```csharp
internal sealed record McpArtefact(string Kind, string MimeType, string Base64, int Ordinal);

internal static class McpContent
{
    // Today's behaviour, unchanged: every binary entry becomes a sentence.
    public static string Flatten(JsonElement result, string serverName);

    // The round-4 path: a saveable entry asks the sink, and the sink's sentence is used verbatim.
    public static string Flatten(JsonElement result, string serverName, Func<McpArtefact, string> save);
}
```

The alternative — return artefacts and render afterwards — forces either a mutable public `McpCallResult`
carrying an internal report, or a second rendering pass in the adapter that re-implements the 009 table.
Both are worse than one delegate. The delegate also gives the tests a seam: a `save` that just returns
`"saved"` proves the flattening rule without touching the file system.

**The public surface does not move.** `McpServerConnection.CallAsync(tool, arguments, timeout, ct)` keeps
its signature and delegates with `save: null`, so it is still "the protocol, no workspace" — and 013's
dogfood test, which calls that overload and compares text with `SearchCodeTool`, is untouched. The adapter
uses a new `internal` overload that adds the delegate. No `PublicAPI.Unshipped.txt` entry is needed: the
analyzer covers `SharpCoreAgent.Domain` only, and this round adds nothing there.


## The sink

`McpArtefactSink` (new, `src/SharpCoreAgent.Infrastructure/Mcp/McpArtefactSink.cs`) owns every decision the
spec's FR-004 to FR-009 make, in one place, and never throws:

```csharp
internal sealed class McpArtefactSink(
    Workspace workspace,
    ApprovalMode approval,
    string server,
    string tool,
    McpClientOptions options)
{
    public IReadOnlyList<string> Written { get; }   // workspace-relative, in save order
    public string Save(McpArtefact artefact);       // the sentence that replaces the description
}
```

`Save` is a sequence of refusals, each naming its own reason and none of them fatal. The sentences are
FR-007's, verbatim, because the spec fixed the wording and the tests assert it:

| Step | Refusal text appended to the description | FR |
|---|---|---|
| approval is not `Ask`/`AllowAll` | `· not saved: this session may not write` | FR-010 |
| `MaxArtefactsPerCall` already reached | `· not saved: the call already saved 8 artefacts` | FR-006 |
| `base64.Length / 4 * 3` above `MaxArtefactBytes` | `· not saved: larger than 8 MiB` — checked **before** decoding | FR-006 |
| `Convert.FromBase64String` throws `FormatException` | `· not saved: the data is not valid base64` | FR-009 |
| decoded bytes above `MaxArtefactBytes` | the same ceiling, measured rather than estimated | FR-006 |
| `mimeType` outside FR-004's five | `· not saved: image/tiff is not a format this client saves` | FR-004 |
| `PathSafety.TryResolve` refuses the path | `· not saved: the workspace refused the write (<reason>)` | FR-009 |
| `Directory.CreateDirectory` / `File.Move` throws | the same sentence, with the exception's message | FR-009 |

A saved entry replaces the description with FR-007's form — base64 **count** in, no base64 out (FR-011),
which is why the sentence stays short enough that `TextLimits.Bound` never eats the path:

```text
[image: image/png · 48213 base64 characters → .sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png (36.4 KiB)]
```

The estimated-size check is the detail worth keeping: base64 inflates by 4/3, so a 40 MiB capture is a
56 MiB string, and a client that decodes first has already spent the memory the ceiling exists to bound.

## The path

```csharp
var directory = Path.Combine(workspace.Root, ".sharpcoreagent", "artifacts");
var relative  = Path.Combine(".sharpcoreagent", "artifacts", $"{server}-{tool}-{n}.{ext}");
```

- `<server>` and `<tool>` are the names the model already sees, so the file is findable from the transcript
  without a lookup table: `npx-browser_take_screenshot-1.png` for `npx_browser_take_screenshot`.
- Both halves are folded by `McpToolName.Fold` — ASCII letters and digits kept, everything else turned into
  `_` — and joined with `-`. Reusing the fold matters twice: a server name comes from a developer's command
  line and must not become a path separator, and the stem is then identical to the tool name minus the
  underscore the model's prefix already has.
- `n` starts at the entry's ordinal among the saveable artefacts of that call, counting from one (FR-003),
  and advances while the name exists on disk (FR-005) — which is also why the same answer in the same
  directory always yields the same names (FR-014), with no state carried between calls.
- The write mirrors `WriteFileTool`: temporary file in the destination directory, then `File.Move`. A
  cancelled or failed write leaves no half a PNG, and a refused write leaves the directory as it was.
- Confinement is structural (the name is derived, never model-supplied) and still checked:
  `PathSafety.TryResolve` on the combined path, so a server whose tool is named `..\..\x` is refused by
  the same code that guards every other write.
- `Directory.CreateDirectory` runs on demand, so a workspace that never sees a screenshot never gets a
  `.sharpcoreagent` directory. `.gitignore` line 14 already ignores the parent.

## The ceilings

`McpClientOptions` gains two properties beside the existing six, with the same doc-comment shape and
`Validate()` bounds:

| Property | Default | Range | Why this number |
|---|---|---|---|
| `MaxArtefactBytes` | `8 * 1024 * 1024` | 1 KiB – 64 MiB | a full-page PNG of a dense page is a few MiB; 8 leaves room without letting one call fill a disk |
| `MaxArtefactsPerCall` | `8` | 1 – 64 | a screenshot, a snapshot and a handful of images is a normal answer; a hundred is a server bug |

`CodeIndexFormat.Size(long)` already renders `0.4 KiB` / `9.4 MiB`, so the sentences reuse it instead of
growing a second formatter.

## The effects

FR-008 is the one place where this round contradicts what 009 built, deliberately: `browser_take_screenshot`
is annotated `readOnlyHint: true`, so `McpTool.Safety` is `ReadOnly` and 009's adapter reports no effects —
which would make a turn that wrote a PNG claim it changed nothing. Now:

- `Safety` stays whatever the server annotated, because prompt behaviour must not change under a developer.
- `ExecuteAsync` appends `new ToolEffect(ToolEffectKind.Created, relative)` for each path the sink wrote,
  and keeps `CommandRun` for destructive calls. `/files` and the observer's tool line therefore show the
  screenshot, which is what makes it a file the developer can open.
- A refused or degraded save contributes no effect, because nothing was created.

## What does not change

- **The system prompt.** No new rule: the sentence already carries the relative path, and `read_file` opens
  it. A rule saying "screenshots are files" would cost tokens in every session to describe one sentence the
  model is looking at anyway. If an artefact rule is ever wanted, it belongs in the round that owns the
  prompt, not here.
- **The command line.** No flag, no settings key, no environment variable, no new tool name, no new
  dependency (FR-013). `--mcp "npx -y @playwright/mcp@latest"` is the whole configuration.
- **The approval model.** The write happens inside the call the developer already approved, and it cannot
  escape the workspace (FR-002), so there is nothing to steer and no second prompt to ask.
- **`McpCallResult`, `McpToolInfo`, `McpToolName`, `ITool`, `ToolResult`, `Workspace`.** No signature moves.
- **The wire and the `--json` event schema.** `ToolResult.Content` carries the sentence, never base64
  (FR-011); the event schema is unchanged because the effects it already carries now name the artefact.
- **The index and the listing tools.** `.sharpcoreagent` stays invisible to `IgnoredPaths`,
  `WorkspaceFileScanner` and `IndexPaths` exactly as it is today (FR-012): a screenshot is never indexed,
  searched, or listed by `list_files`, and `.gitignore` line 14 keeps it out of the repository.

## The tests

Phase 1 (code), `tests/SharpCoreAgent.Tests/Infrastructure/McpArtefactTests.cs` plus additions to
`McpToolTests.cs`. Every test uses the existing `ScriptedMcpServer` harness and a temp workspace:

1. An `image` entry is written, the bytes match, the sentence names the relative path, and the result
   carries `Created` — on a **read-only tool** (FR-008).
2. A `resource` entry with an image `blob` is written by the same rule (FR-001, second shape).
3. A second call advances to `-2` and the first file's bytes are unchanged (FR-005).
4. `ApprovalMode.ReadOnly` — the default of `ToolInvocationContext` — writes nothing and says so (FR-010).
5. `MaxArtefactBytes` is enforced *before* decoding, and again on the decoded length (FR-006).
6. `MaxArtefactsPerCall: 1` with two images: one saved, one described with the ceiling named (FR-006).
7. `image/tiff` and invalid base64 are described with the reason, and no file appears (FR-004, FR-007).
8. A file named `.sharpcoreagent` in the workspace makes `CreateDirectory` throw: the call still succeeds,
   returns the server's text plus a sentence, and the transcript is not an exception (FR-009).
9. Two different workspaces over the same answer produce the same relative name (FR-014).
10. `Flatten(json, "npx")` — 009's three existing flattening tests — still pass, unedited.

The `Context()` helper in `McpToolTests` builds `ToolInvocationContext` with the default `ReadOnly`, which is
why test 1 must pass `ApprovalMode.Ask` explicitly and test 4 is the default: the two cases differ by one
`init` property, and a plan that forgot it would make every save test fail for the wrong reason.

## Phases

| Phase | Scope | Done when |
|---|---|---|
| 1 | `McpArtefact`, `McpArtefactSink`, the `Flatten` overload, the internal `CallAsync`, `McpClientOptions`, `McpTool` effects, the ten tests | build and suite green in Release, US1–US5 pass, no existing test edited to fit |
| 2 | `README.md` browser section, `HelpText`'s `--mcp` paragraph, `contracts/mcp-content.md`, the cross-reference in `009`'s FR-009 | the contract file states the amended rule, `009` points at it, and a developer can copy one command out of the README |

## Where the round is easy to get wrong

- **The `009` content table and this round's `contracts/mcp-content.md`.** `009`'s `plan.md` lists `image` →
  `[image: … · <n> base64 characters]`. After this round that row is only true when the sink refuses, so the
  new contract states "described **or** saved", and `009`'s FR-009 gains the cross-reference. Without both,
  two specifications contradict each other on the page while the code agrees.
- **The dogfood test in `013`.** It asserts that `CallAsync`'s text equals `SearchCodeTool`'s. If the
  internal overload ever became the public one with a nullable delegate, that test would still pass — but a
  caller with no workspace would silently get descriptions, so the split is a decision, not an accident.
- **`TextLimits.Bound`.** The path replaces a longer description, so bounding still happens after flattening
  and the saved file is never touched by it — a bound applied to the artefact instead of the sentence would
  produce exactly the partial file FR-009 forbids.
- **Cancellation.** `File.Move` is not cancellable; the write is small and the token is checked before it.
  A cancelled call must not leave the temp file, which is the same `TryDelete` idiom `WriteFileTool` uses.

## Complexity Tracking

| Decision | Cost | Why it is worth it |
|---|---|---|
| A delegate threaded from the adapter into `Flatten` | one extra parameter on two internal members | the sentence must name the file, and only the writer knows the name; the alternative is a second rendering pass or a mutable public result |
| A second `CallAsync` overload, internal | one 4-line delegating method | it keeps the public surface and 013's dogfood test exactly as they are, and it makes "no workspace, no artefacts" a property of the type |
| Artefact logic in its own type | ~150 lines in one file | approval, ceilings, MIME, naming and IO are five failure modes; putting them in `McpContent` would make the flattener stateful and untestable without a disk |
| Two more ceilings on `McpClientOptions` | two properties and two `Require` calls | an unbounded artefact path is a disk-filling bug waiting for the first 200 MiB screenshot |
| `Created` effects for a `ReadOnly` tool | one contradiction with 009's rule | a turn that wrote a file and reported nothing is a lie the developer cannot see; the annotation stays as the server wrote it |
| Reusing `CodeIndexFormat.Size` | a cross-feature call | a second byte formatter is how `9.4 MiB` and `9.44 MiB` end up in the same transcript |

## As built

Landed 2026-10-30, both phases, in the order the plan set them: the seam and the sink, then the ten tests, then
the surface. What follows is measured, not remembered.

**Build and suite** (T028, `Release`, this machine): `dotnet build .\SharpCoreAgent.slnx -c Release
--no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`. `dotnet test .\SharpCoreAgent.slnx -c Release`
→ `total: 1200, succeeded: 1200, failed: 0, skipped: 0, 12s 786ms`. The delta from the 1190 that `011` closed on
is exactly the ten tests of Phase 3, and no existing test was edited to fit (T022): `009`'s three flattening
tests in `McpToolTests` and `013`'s `McpServerDogfoodTests` text equality with `SearchCodeTool` are inside that
green run.

**The ten tests** are one file, `tests/SharpCoreAgent.Tests/Infrastructure/McpArtefactTests.cs`, over the
existing `ScriptedMcpServer` and a temp workspace, each naming its SC in a leading comment: image saved and
named (SC-001, SC-002, FR-008), resource blob saved and text resource unchanged (FR-001), second call advances
and never overwrites (SC-005, FR-005), read-only refusal (SC-003, FR-010), the byte ceiling at 1024 with 1024
saved and 1025 refused (SC-004, FR-006), the per-call ceiling (FR-006), `image/tiff` plus the other five accepted
spellings (FR-004), invalid base64 (SC-004, FR-009), and the same answer in the same state giving the same name
(FR-014).

**The mutation proof** (T021, run rather than described): `McpTool.ExecuteAsync`'s call was changed from
`sink.Save` to `null` and only `AnImageEntryIsWrittenAndTheTextNamesIt` was run — it failed at
`Assert.Single(harness.Files())` with `Assert.Single() Failure: The collection was empty`, which is the whole
claim of this round in one line: no file, and the same change empties `sink.Written`, so no `Created` effect
either. The line was read back as `sink.Save` and the ten artefact tests ran `[+10/x0/?0]` again.

**A live check that did run** (T030 — not the fallback the task allowed). `node v22.19.0` and `npx 10.9.3` are
on this machine, so:

```text
sharpcoreagent run "Open https://example.com in the browser, take a screenshot, and report the exact path of
  the saved file." --workspace <temp> --provider deepseek --approval allow
  --mcp "npx -y @playwright/mcp@latest --browser msedge"
```

The turn called `npx_browser_navigate` (814 ms, title `Example Domain`) and `npx_browser_take_screenshot`
(98 ms), and the sink wrote `<temp>/.sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png`, 44 912 bytes,
first eight bytes `89 50 4E 47 0D 0A 1A 0A`. Three things the run taught, which no scripted server could:

- **The screenshot the model reads is the description's sibling, not the browser's own file.** Playwright saved
  its copy under its own `--output-dir` (`.playwright-mcp/page-….png`, also 44 912 bytes); the transcript
  carries only the sentence with the artefacts path, exactly as §"The path" says. The model could not confirm
  the file itself and had to spend three `run_shell` calls doing it, which is FR-011's limit seen from the other
  side, not a defect.
- **The server's cwd is not the workspace**, so the two copies can land in different trees. Ours is always
  `<workspace>/.sharpcoreagent/artifacts/`, which is the property the spec asked for; the server's is wherever
  its process started, and nothing in this round moves it.
- **`--browser msedge` was passed from the start**, because the default channel wants a browser Playwright
  downloads for itself. The run did not try the default, so that is a choice this check made, not a finding
  about it: the browser is the developer's install, which is what the README says.

**The four-path diff** (T029): `git diff --stat` over `src/SharpCoreAgent.Domain/`,
`src/SharpCoreAgent.Application/CodingAgent.cs`, `src/SharpCoreAgent.Indexing/` and
`src/SharpCoreAgent.DependencyInjection/` reports 6 files, `+321 −9` — `CodingAgent.cs`,
`ServiceCollectionExtensions.cs`, `SharpCoreAgent.DependencyInjection.csproj`, `SharpCoreAgentOptions.cs`,
`Domain/Agents/AgentReport.cs` and `Domain/PublicAPI.Unshipped.txt`. Every hunk belongs to a round that landed and
is still uncommitted in this tree — `011`'s `TaskTool` registration and prompt, `015`'s `FSharpToolsEnabled` and its
project reference, `012`'s `VerifyGate` calls and `AgentReport.IsUnverified` — and none to this round: the round's
diff is `Mcp/` in `Infrastructure`, one paragraph in `HelpText.cs`, the README, this spec's contracts and `009`'s
two pointers. The literal zero the task asked for arrived through the commit instead of an edit: rounds 4, 5 and
7 are one commit, `a447a4d`, and `git diff --stat HEAD` over the same four paths is empty.

**One thing did not survive the plan verbatim: T016's second half.** The plan asked for a payload whose
*estimated* size passes and whose decoded size does not. With `Convert.FromBase64String` there is no such
payload: the estimate `Base64.Length / 4 * 3` is exact for valid base64 once the trailing `=` padding is
subtracted, and whitespace inside the base64 only makes `Length` larger, so it fails earlier rather than later.
The test that earns its place is the boundary instead — `MaxArtefactBytes = 1024` saves exactly 1024 bytes and
refuses 1025 — and writing it is what found the bug the naive estimate had: 1024 bytes and 1025 bytes both
encode to 1368 characters (`Length / 4 * 3` = 1026 for each), so the unpadded estimate refused an honest 1 KiB
screenshot and could not tell the two sizes apart at all; subtracting the `=` padding (2 and 1 respectively) is
what makes the ceiling exact. That subtraction is the only code change Phase 3 forced.

**What a last audit found open, and what it closed.** The audit asked one question of the tree rather than of the
plan: which statements about this round are false today? Inside `010` nothing was: FR-012 needed no new test,
because the invisibility it promises is already asserted — `IgnoredPaths.DirectoryNames` is pinned with
`.sharpcoreagent` in it (`ToolTests`), `WorkspaceFileScannerTests` writes `.sharpcoreagent/index/Own.cs` and
asserts it is not scanned, and both traversals consult `IgnoredPaths.IsIgnored` (`ListDirectoryTool`,
`SearchTextTool`). Outside `010` two were, and both are corrected rather than recorded: `003`'s phase table still
said "P5 MCP client (opt-in) — not started" and its status line stopped at P4, although `003`'s own T040 is
ticked and `009` built it; and `README` still gave 1,112 as the suite size in the coverage paragraph while the
round's own bullet said 1200. The five refusals (T031–T035) and the five "Open, and why" entries stay open on
purpose, and `specs/000-parity-roadmap.md`'s `## Open work` table is where they are recorded — round 4's row
names all five and says which one would matter.

