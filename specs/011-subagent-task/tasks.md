# Tasks — 011 `task`: delegation with an isolated transcript

Legend: `[X]` done and covered by evidence in this change; `[ ]` not started, kept here so the next pass has a real
list rather than a memory. Paths are as they exist in this repository: the tool lives in
`src/SharpCoreAgent.Infrastructure/Tools`, not in an `Infrastructure/Indexing` folder that never existed.

## Phase 0 — the probe, before the plan

- [X] T001 `%TEMP%\sca-011-probe` (outside the repository, `net11.0`, M.E.DependencyInjection 11.0.0-rc.2): the draft's
  shape — a tool registered as `ITool` whose factory calls `GetServices<ITool>()` — fails with
  `InvalidOperationException: A circular dependency was detected for the service of type 'IEnumerable<ITool>'`
  (`exit=-532462766`). So `IEnumerable<ITool>` in `TaskTool`'s constructor is a startup crash, not a design taste.
- [X] T002 The same probe with a `Func<IReadOnlyList<ITool>>` read at execution time: `registry holds 2: plain,deferred`
  and `child sees: plain` (`exit=0`). The deferred source resolves, and filtering by name is what keeps `task` out of
  the child's view.
- [X] T003 Four questions answered by reading one line each, recorded in `plan.md`: the context carries `Model` and
  `Approval` (`CodingAgent.ExecuteAsync`); `FinalText` is set on `Completed` (`AdvanceAsync`); `--mcp` tools are visible
  only through `IToolRegistry` (`AddMcp` replaces the registration); the gate reads `result.Effects` (`VerifyGate`).
- [X] T004 `src/SharpCoreAgent.Infrastructure/Tools/TaskTool.cs.pending` moved to
  `specs/011-subagent-task/TaskTool.cs.pending` unchanged, which is what the roadmap's round-5 decision says happens
  "when that spec is written". It is not compiled from either location. It stays in that folder now that the round has
  closed — the roadmap's `## Round 5` "The draft" paragraph records that as the decision, not an oversight: it is the
  design of record, and nothing builds or loads it.

## Phase 1 — the tool

- [X] T005 `src/SharpCoreAgent.Infrastructure/Tools/TaskTool.cs`: `Name`, `MaxSubAgentIterations` (24),
  `DefaultSubAgentIterations` (12), `MaxDescriptionChars` (120), the schema from `contracts/task-tool.md`,
  `Safety = Destructive`, the constructor as `(IChatModelResolver, Func<IToolRegistry>, AgentOptions)`, and
  `ExecuteAsync` doing exactly five things — read arguments, build the child session, build the child registry, run one
  turn, render the result with the child's effects attached. It implements `IToolPreview`.
- [X] T006 `src/SharpCoreAgent.Infrastructure/Tools/NoPromptApprover.cs`: refuses everything, with the remark that says
  why it is unreachable and why it exists anyway (and why it is not the container's `DenyAllApprover`).
- [X] T007 `src/SharpCoreAgent.Infrastructure/Tools/TaskFormat.cs`: the header (`task · <description> · N model calls ·
  T tokens`), the start/finish narration lines, the non-completed sentences and the approval preview, so the tool, the
  tests and the contract cannot word the same fact four ways.
- [X] T008 `tests/SharpCoreAgent.Tests/Infrastructure/TaskToolTests.cs` — twelve tests over scripted models, 12/12 green:
  the round trip answers `task · callers · 1 model calls · 15 tokens` and the child's text; the parent's transcript
  gains no message from the child (SC-001); the child's registry is this one minus `task`; `max_iterations` defaults to
  12 and the ceiling a caller asks for is the one the child gets; a request above 24 is refused
  (`ToolArgumentException`, `between 1 and 24`) rather than honoured, and the child's budget is clamped to the constant
  anyway; a `Completed` child that said nothing is a failure result (`the sub-agent completed without saying anything.`,
  as `contracts/task-tool.md` words it); a child that could not run is a failure naming `(Failed)`; a blank `prompt` is a
  `ToolArgumentException`; an over-long answer is cut with `IsTruncated`; cancellation propagates as
  `OperationCanceledException`; the preview shows the prompt and the ceiling. The fakes are local to this file because
  the ones in `CodingAgentTests` are private nested types — copying four small ones is cheaper than a test-utility
  project.
- [X] T009 SC-002 as a failing run: with the filter replaced by `new ToolRegistry(_registry().Tools)`,
  `TheChildSeesThisRegistryWithoutDelegationItself` failed at `Assert.Equal(["probe"], advertised)` with
  `Assert.Equal() Failure: Collections differ` (11 passed / 1 failed); the two lines were restored verbatim and the class
  went back to 12/12.

## Phase 2 — permissions and the gate

- [X] T010 The approval matrix, in `TaskToolTests`: parent `Ask` → the child's `write_file` is refused and the file is
  absent from disk; parent `AllowAll` → the same child writes it. One test method per half, sharing one fixture, so the
  only difference in the diff is the approval mode. — `AChildOfAnAskingParentCannotWrite` and
  `AChildOfAnAllowAllParentWrites`, both over one `RunChildWriteAsync(ApprovalMode)` fixture and the real
  `WriteFileTool`, with an identical child script. The asking half also asserts that the refusal reached the child
  (`approval mode: ReadOnly` in the child's last request): an absent file alone would also pass if the tool had never
  been offered to the child at all.
- [X] T011 Effects: a child that created/modified/deleted files and ran a command hands those four effects to the
  `task` result, in order, and its `Read` effects are absent. Asserted on `ToolResult.Effects`, not on the text. —
  `AChildsWritesReadsAndCommandsAreForwardedAsEffects`: one child round calls `write_file`, `read_file`,
  `replace_in_file`, `run_shell` and `delete_file`, and `result.Effects` is exactly `Created notes.txt`,
  `Modified notes.txt`, `CommandRun dotnet build`, `Deleted notes.txt` — that order, no `Read`, and the file is gone
  from the disk afterwards. The shell is a stub reporting what the real tool reports, because the real one starts a
  process and this test is about which effects a parent inherits.
- [X] T012 `tests/SharpCoreAgent.Tests/Application/CodingAgentTests.cs`:
  `AnUnverifiedSubAgentEditIsGatedLikeTheParentsOwnEdit` (parent `Completed`, `IsUnverified` true, exactly
  `MaxVerificationNudges` nudges) and `ASubAgentEditIsClosedByAVerificationThatPassesAfterIt` (a `dotnet_verify` after
  the `task` result closes the gate). Both driven by a `task` result whose effects name a `.cs` file — the tests that
  prove delegation cannot launder an unverified edit. — both are driven by the real `TaskTool` (`DelegatingTool`), whose
  child calls a stubbed `write_file` that reports `Modified src/Thing.cs`, so the effect the gate reads is produced by
  the shipping tool. The child is given no verifier: its own gate is a different test, and leaving `dotnet_verify` out
  keeps the script honest about which turn is being gated.
- [X] T013 Mutation proof for T012: remove `ToolEffectKind.Modified` forwarding (or the effect copy) and record that
  `AnUnverifiedSubAgentEditIsGatedLikeTheParentsOwnEdit` fails at its `IsUnverified` assertion; restore verbatim. —
  with `Where(effect => effect.Kind is not (ToolEffectKind.Read or ToolEffectKind.Modified))` in `TaskTool.Forwarded`,
  that test failed at `Assert.True(report.IsUnverified)` with `Expected: True / Actual: False`
  (`CodingAgentTests.cs:673`, 0 passed / 1 failed). Restored to `!= ToolEffectKind.Read` and confirmed verbatim by
  reading the line back; the two `CodingAgentTests` and the fifteen `TaskToolTests` then pass 17/17.

## Phase 3 — wiring, prompt, docs

- [X] T014 `src/SharpCoreAgent.DependencyInjection/ServiceCollectionExtensions.cs`: one `ITool` registration inside
  `AddTools` with the deferred registry source, and the fifteen-tool remark updated to sixteen. — registered beside
  `DotnetVerifyTool` as `new TaskTool(resolver, () => provider.GetRequiredService<IToolRegistry>(), agentOptions)`,
  with the comment that names `F-1` (a tool cannot be handed the set that contains it) and `F-2` (`--mcp` replaces the
  registry after startup). The remark was already stale by one when this task was written: `012` registered
  `dotnet_verify` without counting it, and the README already called `fetch_url` "the seventeenth tool", so the surface
  was sixteen and is seventeen now. The doc comment says seventeen, names the three rounds the count comes from, and
  points at the test that pins the set.
- [X] T015 `tests/SharpCoreAgent.Tests/DependencyInjection/` (or beside the existing registration tests): the built
  registry contains `task`, the count moves by exactly one, and resolving every `ITool` does not throw — which is
  `F-1`'s regression test in the real container. — `tests/SharpCoreAgent.Tests/Infrastructure/TaskToolRegistrationTests.cs`,
  beside `DotnetVerifyRegistrationTests` and `FSharpToolRegistrationTests`, three tests: the tool is advertised and
  `Destructive`; the sorted seventeen names are the documented set (sixteen before this round) and `task` appears once;
  and two resolutions of every tool return the same `TaskTool`, which is the circular dependency `F-1` was, gone.
  3/3 passed against the default `SharpCoreAgentOptions`, index included, in 1.2 s.
- [X] T016 `src/SharpCoreAgent.Application/PromptLibrary.cs`: operating rule 8, naming `task`, the isolated transcript
  and the one sentence about the approval mode a child inherits. Plus a prompt test that the rule ships in
  `SystemPrompt` and that `SubAgentPrompt` still says the child sees nothing else. — rule 8 names `task`, "nothing else
  of this conversation", "its own transcript", "may write only when this session already allows every tool" and the
  `dotnet_verify` obligation; `TheShippedPromptSaysWhenToDelegate` reads that line out of `SystemPrompt` and finds it in
  every `SystemPromptFor` variant, `TheSubAgentIsToldItSeesNothingElse` holds `SubAgentPrompt` to "not visible to you"
  and "minus delegation itself", and 19/19 `PromptLibraryTests` pass. Two consequences were recorded rather than
  discovered later: `SubAgentPrompt` rule 2 said "verify with run_shell", which the child's own gate does not accept, so
  it now says `dotnet_verify` and a test holds that line; and `ChatLoopTests.NearingTheCeilingSaysSoBeforeTheSessionStops`
  moved its ceiling from 1 400 to 1 700 tokens, because the demo adapter bills characters and the prompt grew a rule.
- [X] T017 `specs/001-coding-agent-core/contracts/tools.md`: one row in the tool catalogue (`task`, `Destructive`) and a
  `## Delegation (011)` section beside the 009 and 015 ones — the seam document lists every tool by safety, and a tool
  that is missing there is a tool the next reader will not know exists. Plus
  `specs/011-subagent-task/contracts/task-tool.md`: schema, arguments, result format, effects, the approval matrix and
  the failure sentences. — the catalogue has the `task` row after `dotnet_verify` (Prompts: the child's four effect
  kinds), the "later rounds keep their own contracts" paragraph points at
  `specs/011-subagent-task/contracts/task-tool.md`, and `## Delegation (011)` records safety, registry, prompt,
  approval, result, effects and what is deliberately not forwarded. `task-tool.md` already carried the schema, the
  argument table, the result format, the effects rule, the approval matrix and the four failure sentences; it was read
  against the landed code, and the refusals it quotes are the ones `ToolArgumentReader` and `TaskFormat` produce.
- [X] T018 `specs/000-parity-roadmap.md` row 5 and the round-5 section, `README.md`: the round's status, and the note
  that `TaskTool.cs.pending` now lives under `specs/011-subagent-task/`. — the roadmap's status line, the parity gap
  list, the round table and `## Round 5` now say landed (2026-10-30) with the evidence, the `F-1`/`F-2` correction and a
  "The draft" paragraph that keeps the `.pending` note; the README lists `task` among the tools, describes the
  delegation with its header line, calls `fetch_url` the eighteenth tool, adds a `## What it does` bullet and replaces
  the "phase 1 of four landed" status bullet with the landed one.

## Phase 4 — close

- [X] T019 `dotnet test SharpCoreAgent.slnx` and record passed/failed/skipped with the duration; `Debug` and `Release`
  builds recorded at 0 warnings, 0 errors. — measured on this machine, in this order. `dotnet build -c Debug
  --no-incremental`: `Build succeeded. 0 Warning(s) 0 Error(s)` in 8.8 s (no-incremental on purpose: an incremental
  build proves nothing about the files it skipped). `dotnet build -c Release`: the same `0 Warning(s) 0 Error(s)` in
  11.2 s. `dotnet test -c Debug`: **1190 total, 0 failed, 0 skipped, 12 s 458 ms**. `dotnet test -c Release`: the same
  **1190 / 0 / 0** in 12 s 875 ms, so SC-006's "no new warnings in `Debug` and `Release`" is answered by both
  configurations and the suite is green in both — one step past what the criterion asked. The suite was 1167 before
  this round, and 1190 − 1167 = 23 is exactly the tests the four phases wrote (fifteen `TaskToolTests`, three
  `TaskToolRegistrationTests`, three `PromptLibraryTests`, two `CodingAgentTests`). The registered tool count moved by
  exactly one: sixteen → seventeen, pinned by `EveryToolResolvesAndTheSurfaceIsTheDocumentedSet`. (`AddTools` registers
  fifteen at `HEAD`; `012`'s `dotnet_verify` is one of the sixteen uncommitted tools the round started from, which is
  the same caveat T020 records.)
- [X] T020 SC-005: `git diff --stat` over `src/SharpCoreAgent.Domain`, `src/SharpCoreAgent.Application/CodingAgent.cs`,
  `src/SharpCoreAgent.Domain/Index` and `src/SharpCoreAgent.Indexing`, recorded as empty. — recorded, with the one
  caveat that makes a literal "empty" impossible to produce honestly: `HEAD` (`ad9ab36`) predates `012` and `015`,
  which are still uncommitted in this working tree, so those two paths do carry a diff that this round did not write.
  The command returns 3 files, 230 insertions, 3 deletions:
  `Application/CodingAgent.cs` 88, `Domain/Agents/AgentReport.cs` 12, `Domain/PublicAPI.Unshipped.txt` 133.
  `Domain/Index` and `SharpCoreAgent.Indexing` are absent from it — literally zero, tracked and untracked alike. The
  other two are attributed, not assumed: every added line in `CodingAgent.cs` is `MaxVerificationNudges`,
  `VerifyGate.IsRequired` / `.Verifies` / `.EditsDotnetSource` / `.UnverifiedDetail`, `state.LastDotnetEdit`,
  `state.LastVerification`, `state.VerificationNudges`, `Log.VerificationRequired`, `Log.TurnCompletedUnverified` or
  `AgentReport.IsUnverified` — `012`'s gate, line for line — and tracing the whole diff for 011's identifiers
  (`TaskTool`, `TaskFormat`, `NoPromptApprover`, `SubAgent`, `sub-agent`, `task`) finds none. `Application/PromptLibrary.cs`
  is outside the four paths on purpose: FR-013 allows one rule there, and rule 8 is that rule. Two of the four paths
  therefore answer SC-005 as written, and the third and fourth answer it as "no line this round wrote", which is the
  claim the criterion exists to support. One hole found while recording: `git diff` cannot see untracked files, so the
  same four paths were also checked with `git status --untracked-files=all`, which is how `Domain/Tools/ToolNames.cs`
  (one constant, `DotnetVerify`) and `Domain/Code/*` were attributed to `012` and `015` instead of being mistaken for
  this round's additions.

## Open, and why

- **Parallel sub-agents.** One `task` call is one child, inline. Parallel children need a merge rule for effects and a
  transcript that shows two live turns; neither exists, and inventing them is its own round.
- **Folding the child's spend into `/cost`.** There is no channel from a `ToolResult` back into `AgentSession` usage.
  The result names the tokens instead (FR-014), which is honest but not a budget.
- **Streaming the child's tokens.** `IAgentObserver` has no nesting identifier, so a child's events cannot be
  attributed to the parent's turn. One line at each end until that changes.
- **A model per child.** The child uses `context.Model`. A `--subagent-model` flag would be the first delegation
  dimension the product configures, and the round does not need it.
- **Resuming or inspecting a child.** The transcript is discarded by design. If a developer one day needs to read one,
  the answer is a `--transcript-dir` for the whole process, not a sub-agent feature.

