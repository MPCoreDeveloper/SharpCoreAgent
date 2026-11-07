# Feature Specification: Capability gap closure

**Feature**: `059-capability-gap-closure`
**Status**: implemented 2026-11-07
**Created**: 2026-11-07

## Problem

A capability-maturity pass was requested across four areas: a stronger verification loop (P0), a better tool
surface for a C# coding agent (P1), an AST-based code index (P2), and context/memory (P3). Assessment against the
current tree shows **P0, P1, P2 and most of P3 are already shipped** across the landed rounds, so a fresh
"maturity" spec that re-states them would duplicate a dozen existing specs. Three concrete gaps remain, and they
are what this spec closes:

1. **Git is read-only.** `git_status` and `git_diff` (`035-git-change-evidence`) let the agent *see* a change but
   not *record* one. The model that edits files cannot stage or commit them, so "commit this" falls back to
   `run_shell` — the arbitrary-command door the approval system exists to avoid for well-shaped actions.
2. **Verification is whole-workspace.** `dotnet_verify` (`012-compiler-oracle`) narrows by `project` but has no
   test-name filter and no way to skip the rebuild. After an edit the model re-runs the entire suite instead of
   the one failing test, which wastes the turn budget and slows the feedback loop the tool exists to shorten.
3. **Memory does not survive a session.** `/compact` (`042-context-fraction-compaction`) and `--resume`
   (`041-session-resume`) manage one transcript, but there is no persistent, per-project record of conventions,
   architecture notes or prior decisions. Every session re-learns what the project already knows.

## What ships

Three independent slices, each shippable, testable and reviewable on its own, in this order:

| Slice | Ships | Safety |
|---|---|---|
| 1 · Git write tools | `git_branch` (read), `git_stage`, `git_commit`, `git_branch_write` (write) | `ReadOnly` / `Destructive`, approval-gated |
| 2 · Targeted verification | `dotnet_verify` gains `filter` and `no_build` | unchanged `Destructive` |
| 3 · Project memory | `project_memory` (read) + `remember` (write) | `ReadOnly` / `Destructive` |

## What is already shipped (not in scope)

These are recorded here so the spec re-links rather than re-litigates them:

| Area | Shipped round | Evidence |
|---|---|---|
| Structured build/test reports with diagnostics fed back | `012-compiler-oracle` | `dotnet_verify` (exit code, warnings, per-diagnostic lines, test counts, duration) |
| Per-language turn gate | `037-verification-profiles`, `040-runtime-verifiers` | `IVerificationTool` / `VerifyGate`, `python_verify`, `node_verify` |
| Read-only Git evidence | `035-git-change-evidence` | `git_status`, `git_diff` |
| AST-based C# intelligence | `012`, `034-csharp-compiler-oracle` | `file_diagnostics`, `uses_at`, `signature_at` |
| Symbol lookup / references / docs / cues | index rounds `025`–`032`, `058-framework-cues` | `find_symbol`, `find_references`, `code_context`, `find_docs`, `find_cues` |
| Compaction + session resume | `042-context-fraction-compaction`, `041-session-resume` | `/compact [n]`, `--compact-at`, `--resume` |

## User stories

### 1. Record a reviewed change without the shell (Priority: P1)

A session has edited and verified files; the developer says "commit that". The agent stages the paths it changed,
commits with a message it proposes, and the developer sees the commit subject and confirms it at the approval
prompt — the same gate a file write already passes through.

**Why this priority**: it is the largest concrete gap, it needs no roadmap decision reversed, and it removes the
main remaining reason the model reaches for `run_shell`.

**Independent test**: in a temporary repository, an `allow` session runs `git_stage` then `git_commit`, and the
commit appears in `git log`; an `ask` session prompts before the write; a `readonly` session is refused. No
`run_shell` is involved.

### 2. See the branch this change will land on (Priority: P1)

A session needs to know its branch, create one, or (rarely) delete one it created, without a shell.

**Independent test**: `git_branch` lists branches (read-only, available in Plan and Review); `git_branch_write`
creates one behind the approval gate; deleting is refused in `readonly` and prompts in `ask`.

### 3. Run the failing test, not the whole suite (Priority: P2)

After an edit, `dotnet_verify kind=test filter=FullyQualifiedName` runs only the matching test(s); `no_build=true`
re-runs against the already-built binaries. The model sees "1 test failed here" in seconds instead of re-running
1,500 tests.

**Why this priority**: it shortens the verification loop the program is built around, and it is a small, additive
change to an existing tool rather than a new surface.

**Independent test**: a fixture test project reports the one filtered test, and `no_build` skips compilation — but
a `no_build` run never satisfies the compile gate.

### 4. Recall a project decision next session (Priority: P3)

A session learns a convention ("tests are xUnit with fluent assertions", "this module is frozen"), writes one
note, and a later session reads it before making the same mistake.

**Why this priority**: it is the largest of the three and the only one that touches a recorded roadmap decision
(rules files were refused), so it is scheduled last and scoped conservatively.

**Independent test**: a note written in one process is read back in a fresh process from the same workspace; the
store is bounded, git-ignored and never read automatically as ambient authority.

## Functional requirements

### Slice 1 — Git write tools

- **FR-001**: `git_stage`, `git_commit` and `git_branch` are `ITool`s with their own JSON Schema, a `ToolSafety`
  of `Destructive`, and recorded effects — the same contract as every existing tool (`001-coding-agent-core`).
- **FR-002**: The argv is fixed and the model supplies only data: `git_stage` takes one or more workspace-relative
  paths (or a closed "all tracked changes" scope); `git_commit` takes a bounded message and a closed scope;
  `git_branch_write` takes a closed operation (`create`, `delete`) and a single branch name, and `git_branch` is a
  fixed `--list` read. No arbitrary Git command, option, revision expression or `-c` config injection is accepted.
- **FR-003**: A path or branch name passes `PathSafety.TryResolve` (or an equivalent name confinement) before Git
  starts, and is always placed after `--`. A name or path outside the workspace is refused before any write.
- **FR-004**: Writes run through the existing `ShellProcessRegistry` with no new `Process.Start` (Principle III),
  no pager, no terminal prompt, `GIT_OPTIONAL_LOCKS=0` dropped for the write path, secrets removed, and the
  repository-redirection / config-injection variables removed exactly as `GitCommand` does today.
- **FR-005**: In `readonly` approval all three are refused; in `ask` each write prompts once with a summary that
  names the tool and shows the effect (the commit subject and affected paths; the branch name). `/approval`
  semantics (`y`/`t`/`a`) apply unchanged.
- **FR-006**: `git_commit` proposes, and the developer can refuse, the message; the commit author identity comes
  from the repository/user config, never from a model-supplied `-c user.name/user.email`.
- **FR-007**: `git_branch` (listing) is `ReadOnly`, so it is surfaced to Plan/Review the way `git_status`/`git_diff`
  are, without granting a write path; creating and deleting is the separate `Destructive` `git_branch_write`.
- **FR-008**: Failure, timeout, non-repository workspace and missing-executable paths return stable error results
  (never exceptions), matching `git_status`/`git_diff`.
- **FR-009**: `--no-git` removes the new tools together with the existing two and their prompt rule, so a host that
  opted out of Git stays opted out.

### Slice 2 — Targeted verification

- **FR-010**: `dotnet_verify`'s schema gains `filter` (a test-name filter passed to the test host as `--filter`,
  build kind refuses it) and `no_build` (boolean; passes `--no-build` to a `test` run, refused for `build`). The
  existing `kind`, `project` and `timeout_seconds` are unchanged.
- **FR-011**: A filtered or `no_build` run still returns the same structured report shape (`DotnetVerifyReport`):
  exit code, passed/failed, warnings, diagnostics, test counts and duration.
- **FR-012**: A `no_build` test run MUST NOT close the .NET compile gap: a run that skips compilation reports a
  verdict that `VerifyGate` does not treat as a build, so a turn that edited `.cs` and only re-ran tests without
  building is still marked unverified. A `kind=test` run *with* compilation, and `kind=build`, close it as today.
- **FR-013**: A `filter` value that matches no test is a failure naming the filter, not a silent pass — so a typo
  in a test name cannot read as "green".
- **FR-014**: No new process, network call or command host is added; the arguments travel in
  `ProcessStartInfo.ArgumentList` exactly as `CreateStartInfo` does now.

### Slice 3 — Project memory

- **FR-015**: The read/write surfaces are two `ITool`s with schemas and recorded results — `project_memory`
  (`ReadOnly`: list/get) and `remember` (`Destructive`: create/replace, approval-gated like every write). They are
  split because `ITool.Safety` is a fixed property and one tool cannot be both classes.
- **FR-016**: The store lives under `${workspace}/.sharpcoreagent/` (already git-ignored by the constitution's
  transcript rule), never in a repository-tracked rules file, and never outside the workspace.
- **FR-017**: A note is bounded (size and count ceilings, stated in the tool result when they truncate), and
  content is written verbatim without evaluating or executing it. Secrets are not special-cased in transit but the
  store is treated like a transcript for redaction/description (Principle VIII).
- **FR-018**: The memory is opt-in ambient context, not authority: the loop may *offer* a summary of stored notes
  to the model; it never makes a stored note an operating rule, and `readonly`/plan turns read it without gaining
  a write path. The exact offering/loading rule is [NEEDS CLARIFICATION] C3.
- **FR-019**: A stored note has a name, a body and a timestamp; reading with no name lists notes, reading a name
  returns one, appending a new name creates and an existing name overwrites or appends per the tool's closed
  contract.

## Non-goals

- Arbitrary Git (`log`, `blame`, `reset`, `checkout`, `rebase`, `fetch`/`pull`/`push`, `stash`, `cherry-pick`,
  `revert`, `tag`, `merge`). The slice adds exactly stage, commit and branch, nothing else.
- Rewriting or broadening the existing `git_status`/`git_diff` tools beyond what their fixed shape already is.
- A `dotnet_verify` that selects a *different* project per test, a live test explorer, coverage, or restoring
  packages (`--no-restore`).
- Changing the four-provider or demo-provider surface; no provider adapter is touched.
- Consuming or writing `AGENTS.md` / `.clinerules` as authority (see C3). Cross-process undo, and any memory that
  reads or executes stored text as instructions.

## Success criteria

- A temporary repository records a commit produced by `git_stage` + `git_commit` with no `run_shell` call; the
  commit's subject and paths appear in the approval prompt; `readonly` refuses and `ask` prompts.
- `git_branch` lists, creates and (when allowed) deletes a branch; a name escaping the workspace is refused.
- `dotnet_verify kind=test filter=<name>` reports only the matching test(s), and a filtered `no_build` run does
  not close the compile gate for a prior `.cs` edit.
- A note written by one process is read by a fresh process in the same workspace; the store is bounded, git-ignored
  and never loaded as an operating rule.
- `dotnet build .\SharpCoreAgent.slnx -c Release` finishes at 0 warnings, the full suite is green, and the
  existing `GitToolTests`, `DotnetVerifyToolTests`, `DotnetVerifyReportParserTests`, `CodingAgentTests` and
  `VerifyGate` tests remain green unchanged.

## Assumptions

- **A1 (Git staging)**: [NEEDS CLARIFICATION — C1] the minimal, safe surface is `git_stage` + `git_commit` rather
  than a `--all`/`-a` flag that commits unrelated tracked files; the default here is an explicit `git_stage`, so
  the developer always sees exactly what will be committed.
- **A2 (branch delete)**: [NEEDS CLARIFICATION — C2] `git_branch` ships `list`, `create` and `delete`; `delete`
  refuses the currently-checked-out branch and prompts in `ask`. The default is to include `delete` behind the
  gate, since a created-but-unwanted branch is the exact case the model otherwise leaves littered.
- **A3 (memory scope)**: [NEEDS CLARIFICATION — C3] memory is a *private* `.sharpcoreagent/` store the agent reads
  and writes through a tool, and does **not** reverse the roadmap's refusal of `AGENTS.md`/`.clinerules` as ambient
  authority. The default does not auto-load notes into the prompt; it exposes `project_memory` so the model can ask.
- **A4 (layering)**: no slice adds a line to `SharpCoreAgent.Domain` beyond the tool-name constant pattern already
  used for `dotnet_verify` (`ToolNames`); `Application` sees only the `ITool`/`IVerificationTool` seams; the
  implementations live in `SharpCoreAgent.Infrastructure/Tools` beside their siblings.
- **A5 (providers)**: the demo provider and all four providers are unaffected; these are tool-surface and loop
  changes only.


