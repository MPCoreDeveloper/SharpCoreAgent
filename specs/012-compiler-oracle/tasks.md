# Tasks — 012 compiler oracle

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the next pass
has a real list rather than a memory.

## Phase 1 — the contract, before the process

- [X] T001 `ToolNames.DotnetVerify` in `SharpCoreAgent.Domain.Tools`, so the loop and the tool name the same
  string without `Application` referencing `Infrastructure` (FR-008).
- [X] T002 `AgentReport.IsUnverified`, with the remark that says false is the normal case and true is not a
  failure (FR-009, US3).
- [X] T003 `PublicAPI.Unshipped.txt`: the two `ToolNames` lines and both halves of `AgentReport.IsUnverified`
  (`get` and `init`). `RS0016`/`RS0017` are errors in this repository, so the governed assembly cannot grow
  silently.
- [X] T004 `DotnetVerifyReport` — a record of what the log said: `Succeeded`, `ExitCode`, `Summary`,
  `WarningCount`, `Errors`, `TestsPassed`, `TestsFailed` — and nothing inferred (FR-002).

## Phase 2 — the parser

- [X] T005 `DotnetVerifyReportParser.Parse(exitCode, output)`: `Succeeded` from the exit code plus the
  `Build FAILED.`/`Failed!` markers plus the presence of a diagnostic; `Errors` deduplicated and capped at
  25; `WarningCount` from `N Warning(s)` or from the warning lines; test counts from the last
  `Failed: n, Passed: m`; `Summary` from the first verdict line (FR-002, spec US1).
- [X] T006 `[GeneratedRegex]` patterns rather than string splitting, so the three shapes the SDK prints are
  read the same way on every platform and the parser stays allocation-light.

## Phase 3 — the tool

- [X] T007 `DotnetVerifyTool` (`dotnet_verify`, `ToolSafety.Destructive`) with `kind`, `project` and
  `timeout_seconds`, validated through `ToolArgumentReader` and `PathSafety` (FR-001..FR-003).
- [X] T008 The start info: `ArgumentList`, no shell, `DOTNET_CLI_UI_LANGUAGE=en`, closed stdin, UTF-8
  without a BOM, and `ChildEnvironment.RemoveSecrets` (FR-003..FR-005).
- [X] T009 `ShellProcessRegistry` as the host, with the timeout killing the tree and the cancellation
  translated the way `run_shell` translates it — no third `Process.Start` (FR-006).
- [X] T010 `Render`: the labelled header, the numbered error list, the bounded log, and `IsError` from
  `report.Succeeded` so a failed build cannot close the gate (FR-007, FR-010).
- [X] T011 `CommandFor(kind, project)` as the one place the display command is built, used for the
  `CommandRun` effect and for the header (FR-007).
- [X] T012 `ChildEnvironment`, extracted from `RunShellCommandTool`, so the credential filter is one
  implementation rather than two that can drift (FR-004).

## Phase 4 — the gate

- [X] T013 `VerifyGate` (internal, `Application`): `Verifies`, `EditsDotnetSource`, `IsRequired`, and the
  extension list — `.cs`, `.vb`, `.fs`, `.csproj`, `.vbproj`, `.fsproj`, `.props`, `.targets`, `.sln`,
  `.slnx` (FR-008, FR-010).
- [X] T014 `CodingAgent.TurnState` records the last .NET edit and the last passing verify by index, and
  `AdvanceAsync` nudges at most `MaxVerificationNudges` times before completing with `IsUnverified` and a
  `Detail` (FR-009, US2).
- [X] T015 `PromptLibrary`: rule 5 names `dotnet_verify` instead of proving a .NET edit with `run_shell`,
  the reporting block forbids claiming a build without one, and `VerifyNudge` is the loop's sentence (US4).
- [X] T016 `ServiceCollectionExtensions.AddTools` registers `DotnetVerifyTool` on the shared
  `ShellProcessRegistry` (US4).

## Phase 5 — the evidence

- [X] T017 `CodingAgentTests`: `ATurnThatEditsSourceAndVerifiesCompletesVerified`,
  `ATurnThatEditsSourceAndNeverVerifiesIsCompletedButUnverified`,
  `ATurnThatEditsNoDotnetSourceIsNotGated`,
  `AVerificationThatRanBeforeTheEditDoesNotCloseTheGate` and
  `AVerificationThatFailedDoesNotCloseTheGate` — the two order-sensitive cases are the reason the gate compares
  indices rather than looking for a name.
- [X] T018 `DotnetVerifyReportParserTests`: a passing build, a failing build with two diagnostics of the
  same text, the warning total, a `dotnet test` summary, an exit code with no log, a localized log, and the
  error ceiling.
- [X] T019 `DotnetVerifyToolTests`: a real compiler run over a broken project (the only test in the suite
  that starts `dotnet`), the refusal of a project outside the root, plus `CommandFor`.
- [X] T020 `PromptLibraryTests`: the shipped prompt names `dotnet_verify`, the sentence that sent builds
  through `run_shell` is gone, and the nudge names the tool.
- [X] T021 `ConsoleObserver` renders `⚠ unverified:` in the text surface and `"unverified"` in the JSON
  report, pinned by three `ConsoleRenderingTests` cases: the line appears, it does not appear when the turn was
  verified, and the JSON carries the flag (US3).
- [X] T022 `README.md` and `specs/000-parity-roadmap.md` updated: the tool, the gate, the suite count, and
  the note that `web_search`, if it is ever decided, takes `014` rather than `012`.

## Deliberately not in this round

- [ ] T023 Roslyn, or any analyzer that answers "does this compile" without the compiler. The process is the
  oracle; a semantic model would also make `SCA0001` a lie about what this repository needs to run.
- [X] T024 `--no-verify`, a settings key, or an environment variable that turns the gate off. **Done for the CLI opt-out in this slice:** `--no-verify` is accepted on `run` and `chat` only and sets `AgentOptions.VerifyEdits=false` for that invocation. It is still not a settings key or environment variable.
- [ ] T025 Automatic retry of a failed build without the model's involvement. The nudge is the retry policy:
  the model sees the diagnostics and decides, which is the work it is paid for.
- [ ] T026 A verification requirement for scripts, SQL or notebooks. Each needs its own oracle, and none of
  them has one in this repository yet.
- [ ] T027 Gating `mcp serve`. The server publishes five read-only tools and has no turn loop; a gate there
  would be dead code pretending to be a guarantee.
