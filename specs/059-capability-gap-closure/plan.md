# Implementation Plan: Capability gap closure

**Feature**: `059-capability-gap-closure`
**Status**: Draft
**Created**: 2026-11-07
**Spec**: [spec.md](spec.md)

## Summary

Three additive slices that close the gaps the spec names, without touching a provider, the index, or the
layer boundaries. Slice 1 gives Git a write path (`git_stage`, `git_commit`, `git_branch`) built on the same
`GitCommand`/`ShellProcessRegistry` the read-only tools use, marked `Destructive` so the existing approval
gate is the whole safety story. Slice 2 adds `filter`/`no_build` to `dotnet_verify` and teaches the turn gate
that a run which skipped compilation is not a passing build (FR-012). Slice 3 adds a private project-memory
store under `.sharpcoreagent/memory/` behind two tools — a read-only one and an approval-gated one — split only
because `ITool.Safety` is a fixed property.

## Technical Context

- **Language/Version**: C# 15 (`LangVersion=preview`), `net11.0` (`SCA0001` floor in `Directory.Build.targets`).
- **Primary Dependencies**: BCL only for these slices. `git` and the `dotnet` SDK are external processes started
  through the existing `ShellProcessRegistry`; neither is a new dependency and neither is bundled.
- **Storage**: project memory is plain UTF-8 files under `${workspace}/.sharpcoreagent/memory/` (the directory is
  already git-ignored by the constitution's transcript rule). No new database, no new package.
- **Testing**: xUnit on Microsoft.Testing.Platform via `dotnet test`; the Git slice uses real temporary
  repositories created with `git init`, the `035` pattern (`GitSandbox`).
- **Target Platform**: Windows, Linux and macOS — the same cross-platform surface as the shell and `dotnet_verify`.
- **Project Type**: existing layered solution (Domain / Application / Infrastructure / DependencyInjection / Cli /
  Indexing). No project is added.
- **Performance Goals**: `filter` + `no_build` make "re-run the one failing test" cost seconds instead of a full
  suite; a memory `list`/`get` is a directory read, not an index query.
- **Scale/Scope**: at most five new tools (`git_stage`, `git_commit`, `git_branch`, `project_memory`, `remember`)
  and two new `dotnet_verify` arguments. Roughly 1,500 lines including tests.

## Constitution Check

*GATE: must pass before the task list is written. Re-checked after design.*

| Principle | How this plan complies |
|---|---|
| I. Model is a port | No provider op is touched; the demo provider and all adapters are unchanged. |
| II. Tools are the only door | Every new capability is an `ITool` with a JSON Schema, a `ToolSafety` class and a recorded `ToolResult`. The loop still calls no `File`/`Process` for these. |
| III. Untrusted output, validated at boundary | No new `Process.Start`. Git runs through `GitCommand`→`ShellProcessRegistry`; `dotnet_verify` keeps its one spawn point. Git argv is fixed and model input is a pathspec/name after `--`, validated first. |
| IV. Every loop has a budget | No loop change. Tool output stays under `TextLimits`; memory notes are size/count bounded and report truncation. |
| V. Streaming default | Untouched; tools are synchronous-minded as today. |
| VI. Evidence over assertion | Each slice ships tests that fail on revert (listed under Verification); no success is claimed without a build/test result. |
| VII. Zero warnings, snapshot surface | Release build at 0 warnings; the one Domain addition (`ToolResult.CountsAsVerification`) and any new public tool land in `PublicAPI.Unshipped.txt` in the same commit. |
| VIII. Secrets stay in the process | Git child still strips secrets and repo-redirect variables; the memory store is treated like a transcript (git-ignored, no network, no phone-home). |
| Workflow gate | `dotnet build` + `dotnet test` in Release at each phase close; Constitution/plan/complexity tracked here. |

No principle is relaxed. The single Domain touch is recorded under Complexity Tracking.

## Architecture

### Slice 1 — Git write tools

The read-only pair `git_status`/`git_diff` and the new write tools share one `GitCommand`. It gains a `write`
mode: the read prefix `--no-optional-locks` is dropped (a commit must write the index and objects), while every
other safety property — no pager, no terminal prompt, UTF-8, secrets removed, repository-redirection/config
variables removed, fixed `-c` prefix, literal pathspecs — is kept. Three new tools sit beside it:

| Tool | Fixed argv (after the shared prefix) | Model data |
|---|---|---|
| `git_stage` | `add -A -- <path>...` | one or more confined workspace paths |
| `git_commit` | `commit --no-verify -m <message> -- <path>...` | a bounded message, optional paths |
| `git_branch` | `branch --list` · `branch <name>` · `branch -d <name>` | closed `op` + one validated name |

**No repository-configured program runs.** The write path adds `-c core.hooksPath=<fixed nonexistent dir>` so no
pre-commit, commit-msg or post-commit hook executes, and `-c commit.gpgsign=false -c gpg.program=false` so a
repository that signs commits does not spawn a signing program. This is the write-path analogue of the read path's
`--no-ext-diff`/`--no-textconv`. A commit therefore records exactly what the model proposed, with the author
identity the user's Git config already defines — never a model-supplied `-c user.*`.

**Name and path confinement.** A path passes `PathSafety.TryResolve` before `git add`/`commit` starts, and travels
after `--`. A branch name is validated with `git check-ref-format --branch <name>` (which also refuses a leading
`-`) before `git branch` runs, so a name can never become an option; the checked-out branch is refused for
`delete`.

**Approval is the existing gate.** All three are `ToolSafety.Destructive`, so `readonly` refuses them, `ask`
prompts once per write (`y`/`t`/`a` unchanged), and `allow` runs them. No approval code changes. `git_commit`
implements `IToolPreview` so the prompt shows the proposed subject and the staged paths instead of a JSON line.

**Rendering and failure.** Success answers with a one-line summary (`committed 3 files: <subject> → <sha>`,
`staged 3 path(s)`, `created branch <name>`); a non-zero exit, a timeout, a non-repository workspace or a missing
`git` is a stable error result carrying Git's bounded stderr, exactly as the read tools already do. `--no-git`
removes all five Git tools and the Git prompt rule together.

**Why `git_stage` and not `commit -a`.** A `-a` commit sweeps every tracked change into the message, including
edits the developer did not review. An explicit stage makes the committed set visible in the prompt and is the
one place `IToolPreview` can show it. The two calls cost two prompts in `ask`, which `/approval t` collapses.

### Slice 2 — Targeted verification

`DotnetVerifyTool`'s schema gains two optional properties; `kind`, `project` and `timeout_seconds` are unchanged:

| Arg | Type | Meaning |
|---|---|---|
| `filter` | string | test-name filter, `test` only; passed as `--filter <value>` |
| `no_build` | boolean | `test` only; passed as `--no-build`, so the already-built binaries run |

`CreateStartInfo` grows the same way it maps `kind`/`project` today: `filter` adds two argv tokens
(`--filter`, value), `no_build` adds one (`--no-build`). `kind=build` refusing either is a `ToolResult.Failure`
naming the incompatibility, before any process starts. `DotnetVerifyReport`/`DotnetVerifyReportParser` are
unchanged — `--filter`/`--no-build` change only how many tests run, not the shape of the log, so the same parser
reads the same summary, counts and diagnostics.

A run whose filter matches no test exits non-zero with a "no test matches" message; the existing
`!report.Succeeded` → `IsError` path turns that into a failure naming the run, so a mistyped test name is a red
result rather than a green one (FR-013).

#### The gate refinement (FR-012)

The gate (`VerifyGate`, `TurnState.Add`) closes a profile's gap on any non-error, non-refused result from the
owning tool after the last matching edit. A `no_build` test run satisfies that today, but it never compiled — so it
must not close the .NET *compile* gap.

The only per-call channel from a tool to the loop is the `ToolResult`. The plan adds one neutral flag to that
record:

```csharp
// SharpCoreAgent.Domain.Tools.ToolResult
public bool CountsAsVerification { get; init; } = true;
```

`DotnetVerifyTool` sets `CountsAsVerification = !noBuild` on its result, and `TurnState.Add` adds
`&& result.CountsAsVerification` to the condition that raises `_lastVerification`. The default `true` leaves every
existing tool and test result exactly as it was, so nothing but a `no_build` run changes behaviour. A
`kind=build`, and a `kind=test` *with* compilation, close the gap as before.

Why a Domain bool and not a tool-text parse: the alternative is `Application` reading prose or command strings out
of `Content`/`Effects`, which is exactly the "knows too much" coupling `012` and `037` avoided. One documented
boolean beside `IsError`/`IsRefused` is the smallest honest channel. It is recorded in Complexity Tracking and
`PublicAPI.Unshipped.txt`.

### Slice 3 — Project memory

`ITool.Safety` is a fixed property, so one tool cannot be read-only for `list`/`get` and destructive for `write`.
The slice therefore ships **two** tools over one store:

| Tool | Safety | Contract |
|---|---|---|
| `project_memory` | `ReadOnly` | `list` note names (with timestamps) or `get` one note by name |
| `remember` | `Destructive` | `write` a named note (create or replace), approval-gated |

**The store.** `ProjectMemoryStore` (Infrastructure) reads and writes `${workspace}/.sharpcoreagent/memory/<slug>.md`.
The name is a slug validated to `[a-z0-9-]` (lowercased, spaces to dashes, other characters refused), so it cannot
escape the directory; the body is UTF-8. Ceilings: 16 KiB per note, 64 notes, both reported when they truncate or
refuse. A note's timestamp is the file's last-write time. The directory is created on first write; `list` on an
absent directory is an empty list, not an error.

**No authority, no prompt growth.** v1 does **not** inject notes into the system prompt and does not auto-read
them — the tools are self-describing through `ToolDefinition.Description`, so the recorded evaluation baseline does
not move (FR-018's "may offer" is deliberately not spent here). A stored note is never executed, evaluated or
treated as an operating rule; it is text the model chose to write and can choose to read. `project_memory` is
`ReadOnly`, so Plan and Review can read notes without gaining a write path; `remember` is refused in `readonly` and
prompts in `ask`, like every write.

**Secrets.** The store is treated like a transcript (Principle VIII): it lives under the git-ignored
`.sharpcoreagent/`, is never sent anywhere, and `remember`'s result echoes the note name and byte count, not the
body.

## Files touched

```text
src/SharpCoreAgent.Infrastructure/Tools/
  GitCommand.cs            # add a write mode: drop --no-optional-locks, disable hooks/signing
  GitStageTool.cs          # new  (Destructive)
  GitCommitTool.cs         # new  (Destructive) + IToolPreview
  GitBranchTool.cs         # new  (Destructive)
  DotnetVerifyTool.cs      # add filter/no_build; set CountsAsVerification
  ProjectMemoryStore.cs    # new  (Infrastructure; bounded slug files)
  ProjectMemoryTool.cs     # new  (ReadOnly  — list/get)
  RememberTool.cs          # new  (Destructive — write)

src/SharpCoreAgent.Domain/Tools/
  ToolResult.cs            # add CountsAsVerification (default true)

src/SharpCoreAgent.Application/
  CodingAgent.cs           # TurnState.Add consults CountsAsVerification
  PromptLibrary.cs         # extend GitRule to name the write tools; no memory prompt in v1

src/SharpCoreAgent.DependencyInjection/
  ServiceCollectionExtensions.cs  # AddGit registers the three writes; AddTools registers the two memory tools

tests/SharpCoreAgent.Tests/
  Tools/GitWriteToolTests.cs        # new  (real temp repos via GitSandbox)
  Tools/ProjectMemoryToolTests.cs   # new
  Tools/DotnetVerifyToolTests.cs    # filter / no_build / build-refusals
  Tools/DotnetVerifyReportParserTests.cs  # unchanged shape still parsed
  Application/CodingAgentTests.cs   # no_build does not close the gate; build does
  Infrastructure/GitToolRegistrationTests.cs  # five git tools, --no-git removes all
  Infrastructure/TaskToolRegistrationTests.cs # the new default count

README.md, HelpText  # the git write tools, the new dotnet_verify args, project_memory
```

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
|---|---|---|
| One Domain bool (`ToolResult.CountsAsVerification`) | The loop must know a `no_build` run did not compile, and the only per-call channel to `Application` is the result. | Parsing `Content`/command strings in `Application` couples the loop to tool prose — the coupling `012`/`037` deliberately avoided. A second profile (`dotnet-test`) doubles the gate's per-profile bookkeeping and risks moving the .NET nudge text. |
| Two memory tools instead of one | `ITool.Safety` is a fixed property; a single tool cannot be `ReadOnly` for reads and `Destructive` for writes. | A dynamic safety class would change the `ITool` contract for every tool and weaken the one input the approval decision trusts. |
| Write-mode `GitCommand` (hooks/signing off) | A commit is the first tool that can run a repository-configured program (hooks, GPG); the read path already refuses that. | Inheriting the repo's hooks would let an unreviewed program run inside an approved "commit". |

No other complexity is added: no new project, no new dependency, no new `Process.Start`, no network.

## Verification and evidence

Each slice closes on the following, recorded in `tasks.md`:

- **Slice 1** — `GitWriteToolTests` over real temp repos: stage+commit produces a commit with the proposed subject;
  `readonly` refuses all three; `ask` prompts; a bad branch name and an escaping path are refused before Git runs;
  a `--no-git` build has none of the five tools.
- **Slice 2** — `DotnetVerifyToolTests`: `filter` runs only matching tests, `no_build` skips the build, and both are
  refused for `kind=build`; `CodingAgentTests`: a `no_build` test after a `.cs` edit is still unverified, a
  `kind=build` after it is not.
- **Slice 3** — `ProjectMemoryToolTests`: write in one process, read in a fresh store instance from the same
  workspace; slug validation, ceilings and truncation; `readonly` refuses `remember` and allows `project_memory`.
- **Whole suite** — `dotnet build .\SharpCoreAgent.slnx -c Release` at 0 warnings and `dotnet test` green; the
  recorded evaluation baseline unchanged (no prompt change beyond the Git rule naming three more tools, recorded in
  the milestone note).



