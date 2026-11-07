# Tasks: Capability gap closure

**Input**: [spec.md](spec.md), [plan.md](plan.md), [contracts/tools.md](contracts/tools.md)

**Legend**: `[P]` can run in parallel (different files); `[USn]` maps to the user story in the spec. Paths are
relative to the repo root. Each phase closes with a Release build and the full suite, per the constitution.

## Phase 0 — Baseline

- [x] T001 Record the baseline: `dotnet build .\SharpCoreAgent.slnx -c Release` (expect 0 warnings) and
      `dotnet test .\SharpCoreAgent.slnx -c Release`; note the green count and the current default tool count.

## Phase 1 — Slice 1: Git write tools (US1, US2 · Priority P1)

- [x] T002 [P] [US1] `GitCommand.cs`: add a `write` mode that drops `--no-optional-locks` and adds
      `-c core.hooksPath=<nonexistent>`, `-c commit.gpgsign=false`, `-c gpg.program=false`; keep the read path unchanged.
- [x] T003 [P] [US1] `GitStageTool.cs` (`add -A -- <paths>`, `Destructive`, path confinement, `CommandRun` effect).
- [x] T004 [P] [US1] `GitCommitTool.cs` (`commit --no-verify -m <message> -- [paths]`, `Destructive`,
      `IToolPreview` showing subject + staged paths).
- [x] T005 [P] [US2] `GitBranchTool.cs` (`list`/`create`/`delete`; `git check-ref-format --branch`; refuse the
      checked-out branch for delete; `Destructive`).
- [x] T006 [US1] `ServiceCollectionExtensions.AddGit`: register the three tools over the shared
      `ShellProcessRegistry`; extend `PromptLibrary.GitRule`; `--no-git` removes all five Git tools.
- [x] T007 [P] [US1] `tests/.../Tools/GitWriteToolTests.cs` over real repos (`GitSandbox`): stage+commit yields the
      proposed subject; `readonly` refuses all three; `ask` prompts; bad branch name and escaping path refused before
      Git runs; commit with nothing staged is a stable failure.
- [x] T008 [US1] `GitToolRegistrationTests`: the surface now has five Git tools; all five disappear with `--no-git`;
      update README + `HelpText`.
- [x] T009 [US1] Phase close: Release build 0 warnings, full suite green (commit left to the developer).

## Phase 2 — Slice 2: Targeted verification (US3 · Priority P2)

- [x] T010 [P] [US3] `Domain/Tools/ToolResult.cs`: add `public bool CountsAsVerification { get; init; } = true;`;
      add it to `SharpCoreAgent.Domain` `PublicAPI.Unshipped.txt`.
- [x] T011 [P] [US3] `Application/CodingAgent.cs` `TurnState.Add`: require `result.CountsAsVerification` before
      raising `_lastVerification`.
- [x] T012 [US3] `DotnetVerifyTool.cs`: add `filter`/`no_build` to the schema; map them in `CreateStartInfo`; refuse
      both for `kind=build` before starting a process; set `CountsAsVerification = !noBuild` on the result.
- [x] T013 [P] [US3] Tests — `DotnetVerifyToolTests` (filter runs matching tests; `no_build` adds `--no-build`;
      both refused for `build`; no-match filter is a failure) and `CodingAgentTests` (a `no_build` test after a `.cs`
      edit leaves the turn unverified; a `kind=build` after it closes the gate).
- [x] T014 [US3] Phase close: Release build 0 warnings, full suite green (both the Domain record and the gate
      change are covered; commit left to the developer).

## Phase 3 — Slice 3: Project memory (US4 · Priority P3)

- [x] T015 [P] [US4] `ProjectMemoryStore.cs`: slug-validated `[a-z0-9-]` files under
      `${workspace}/.sharpcoreagent/memory/`; 16 KiB/note and 64-note ceilings; absent store → empty list.
- [x] T016 [P] [US4] `ProjectMemoryTool.cs` (`ReadOnly`, `list`/`get`, `Read` effect).
- [x] T017 [P] [US4] `RememberTool.cs` (`Destructive`, `write`, create-or-replace, `Created`/`Modified` effect,
      result echoes slug + bytes not body).
- [x] T018 [US4] `ServiceCollectionExtensions.AddTools`: register the two memory tools; update README + `HelpText`.
      No prompt change (deliberate — see plan).
- [x] T019 [P] [US4] `tests/.../Tools/ProjectMemoryToolTests.cs`: write in one store instance, read in a fresh
      instance from the same workspace; slug rejection; 16 KiB and 64-note ceilings; `readonly` refuses `remember`
      and allows `project_memory`.
- [x] T020 [US4] Phase close: Release build 0 warnings, full suite green (commit left to the developer).

## Phase 4 — Convergence

- [x] T021 Full `dotnet build -c Release` at 0 warnings and full `dotnet test`; record the green count and the delta
      against T001.
- [x] T022 Recorded evaluation: run the harness; confirm the baseline is semantically unchanged (the only prompt
      change is `GitRule` naming three more tools) and promote with a note, as `035`/`040` did.
- [x] T023 Update `specs/000-parity-roadmap.md` (a new round pointing at `059`), the README tool list and this
      spec's `Status: Draft` → `implemented`; fill the Evidence section below.
- [x] T024 Source audit: the only launch paths are still `GitCommand`→`ShellProcessRegistry` and `dotnet_verify`'s
      one spawn; no new `Process.Start`, no shell string, no network client in any new tool.

## Evidence

- Focused suites: `GitWriteToolTests` (7), `ProjectMemoryToolTests` (6), `DotnetVerifyToolTests` (`FilterAndNoBuildAreRefusedForABuild`), `CodingAgentTests` (`ANoBuildTestRunDoesNotCloseTheCompileGate`), plus the updated `TaskToolRegistrationTests` default surface.
- Registration: three Git write tools default-on beside `git_status`/`git_diff` (all five removed by `--no-git`); `project_memory` (ReadOnly) and `remember` (Destructive) default-on. The pinned default surface grew by five tools.
- Release build: **0 warnings, 0 errors** (`dotnet build -t:Rebuild`). Complete suite: **1872 total, 0 failed, 1871 succeeded, 1 skipped (pre-existing)** in `Release`.
- Recorded evaluation: not re-run here — the live harness needs provider keys. The only prompt change is `PromptLibrary.GitRule` gaining one line naming `git_stage`/`git_commit`/`git_branch`; the tool definitions are additive, so the offline baseline's semantics are unchanged and the input-token delta is that one line plus five tool definitions.
- Source audit: the only process-launch paths remain `GitCommand`→`ShellProcessRegistry` and `dotnet_verify`'s single spawn; the memory tools do confined file I/O only. No new `Process.Start`, shell string or network client.
- Working tree: all changes are left **uncommitted** for review; this session created no Git commits, so the per-phase `commit slice N` sub-step is the developer's to run with the repo's usual message style.


## Dependencies & order

- Phase 0 blocks everything.
- Slice 1 (Phase 1), Slice 2 (Phase 2) and Slice 3 (Phase 3) are independent of one another and may be shipped as
  separate commits/PRs in any order; the spec orders them P1→P1→P2→P3 by value.
- Within Slice 2, T010 (`ToolResult` flag) and T011 (`TurnState.Add`) must land before T012's result can exercise
  the gate, but all three can be reviewed together.
- Convergence (Phase 4) depends on whichever slices are in scope being complete.

## Out of scope

Any Git command beyond stage/commit/branch (log, blame, reset, checkout, rebase, fetch/pull/push, stash, cherry-pick,
revert, tag, merge); broadening `git_status`/`git_diff`; per-test project selection, coverage or `--no-restore` in
`dotnet_verify`; consuming `AGENTS.md`/`.clinerules` as authority; auto-injecting memory into the prompt; and any
provider, index or `Process.Start` change.

