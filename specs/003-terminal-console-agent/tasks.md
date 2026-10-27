# Tasks — 003 terminal console agent

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the next
pass has a real list rather than a memory.

## Phase 0 — Palette

- [X] T001 Add `ColorChoice` (`auto`, `always`, `never`) and `AnsiTheme` with `Create(choice, redirected, environment)`; `auto` refuses on redirect, `NO_COLOR`, `TERM=dumb`.
- [X] T002 Route every palette call through `Paint`, which returns the input unchanged when disabled.
- [X] T003 Paint a unified diff line by line (`Diff`): `+` green, `-` red, `@@` cyan, headers bold, context dim.
- [X] T004 Colour `ConsoleObserver` glyphs, tool names, arguments, timings and the closing summary; force `Plain` in JSON mode.
- [X] T005 Colour `ConsoleApprover`'s question and refusal.
- [X] T006 Colour `ChatCommand`'s prompt, `/model`, `/approval`, `/usage`, `/files` and the workspace line.
- [X] T007 Colour `ModelsCommand`'s names and the missing-credential note.
- [X] T008 Add `--color` to `CliOptions` with `TryParseColor`, and a usage error with a hint for an unknown word.
- [X] T009 Document `--color` and `NO_COLOR` in `HelpText` and `README.md`.
- [X] T010 Tests: `AnsiThemeTests` (8) covering redirect, `NO_COLOR`, `TERM=dumb`, forced on/off, empty text, diff codes.

## Phase 1 — Fragment edits and diff approval

- [X] T011 Add `UnifiedDiff.Create(path, old, new, context, maxLines)` in `Domain.Text`: prefix/suffix trim, one hunk, 2 context lines, 60-line cap, `-0,0` for a top insertion.
- [X] T012 Add `IToolPreview` in `Domain.Tools` with a nullable, failure-tolerant contract.
- [X] T013 Add `ToolApprovalRequest.Preview` as an optional `init` property.
- [X] T014 Implement `ReplaceInFileTool` (`replace_in_file`): ordinal match, occurrence count, ambiguous refusal, `all`, atomic UTF-8 write, `Modified` effect.
- [X] T015 Implement `DescribeChangeAsync` on the tool from the same read it writes, so preview and write cannot disagree.
- [X] T016 Attach the preview in `CodingAgent.GateAsync`; swallow `ToolArgumentException`, `IOException`, `UnauthorizedAccessException` and `SharpCoreAgentException` as "no preview".
- [X] T017 Render the preview in `ConsoleApprover` before the `[y/N]` question.
- [X] T018 Register the tool in the composition root; update the tool count in the XML doc.
- [X] T019 Prefer `replace_in_file` in `PromptLibrary.SystemPrompt` rule 2.
- [X] T020 Tests: `UnifiedDiffTests` (6) and `ReplaceInFileToolTests` (7).

## Phase 2 — Plan mode

- [X] T021 Add `AgentMode` (`Act`, `Plan`) and `AgentSession.Mode`.
- [X] T022 Add `PromptLibrary.PlanModeInstruction`, prepended per request rather than stored in the session.
- [X] T023 Filter the advertised definitions to `ToolSafety.ReadOnly` in `CodingAgent.DefinitionsFor`.
- [X] T024 Deny a destructive call in `GateAsync` while plan mode is on, with a refusal message that says why.
- [X] T025 Expose `IToolRegistry.Tools` so filtering and enforcement read the same list.
- [X] T026 Add `--mode` to `CliOptions` with `TryParseMode`, and force plan mode on new and resumed sessions.
- [X] T027 Add `/mode [plan|act]` to the interactive loop; document it in `HelpText` and `README.md`.
- [X] T028 Tests: option parsing and mode default/switch.

## Phase 3 — Long sessions

- [X] T029 Add `AgentSession.Compact(n)`: keep leading system messages, keep the last `n`, never start the window on a tool result, insert a summary notice.
- [X] T030 Add `/compact [n]` with a clamped window (1–200, default 8) and a report of how many were dropped.
- [X] T031 Tests: fits, window, orphan guard, invalid window.
- [ ] T032 Add `--resume [path]` that lists the workspace's transcripts when the path is omitted.
- [ ] T033 Add git-based checkpoints per turn and `/undo` to restore the last one.
- [ ] T034 Compact automatically when a turn's prompt exceeds a configurable fraction of the model's context window.

## Phase 4 — Provider depth

- [X] T035 Add `ModelCatalog.TryResolveAlias` (`ds`, `r1`/`reasoner`, `grok`/`g4`, `g3`) and resolve aliases before bare identifiers in `/model`.
- [ ] T036 Re-verify `deepseek-reasoner` tool-call support against the live API and flip `SupportsTools` if it changed. (2026-10-27: the provider's model page no longer lists the identifier at all, so this needs a credential and a request, not a documentation read — see `004/research.md`.)
- [ ] T037 Refresh both catalogues against the providers' current model lists, with a test that pins the identifiers. (Half done in `004`: `deepseek-flash`, `deepseek-v4-pro` and `grok-4.7` are listed and pinned by tests; whether the *defaults* move is `004` T015.)
- [X] T038 Add `/cost` backed by per-model prices, or state in the help that no price is known. (Done in `004-spend-and-fallback`: a dated table that prints `no price known` where it has nothing, and never `$0.00`.)
- [ ] T039 Add an explicit fallback chain for 401/5xx (`--fallback grok:grok-4`) that records both attempts in the report.

## Phase 5 — MCP

- [ ] T040 Opt-in MCP client (`--mcp <command>`), tools registered through the same `IToolRegistry`, approval inherited from `ToolSafety`.

## Phase 6 — Cline parity: streaming shell and multi-block edits

The two gaps that made this agent feel worse than Cline in practice: a shell call that blocks the
whole turn with nothing on screen, and one fragment per edit call. Both are closed here.

- [X] T046 Add `IToolOutputSink` in `Domain.Tools` and an optional `ToolInvocationContext.Output`.
- [X] T047 Add `ToolOutputDelta` and forward a running tool's writes through `CodingAgent.ObserverOutput`, tagged with the call id; a silent tool emits nothing.
- [X] T048 Render live output in `ConsoleObserver` behind a `│ ` gutter on stderr, opening the gutter lazily, closing an unfinished line before the tool line, emitting a `tool_output` JSON line under `--json`, and staying silent under `--quiet`.
- [X] T049 Add `ShellProcess` and `ShellProcessRegistry`: bounded 256 K output buffer, incremental reads, process-tree kill, one-hour ceiling, handles reused as `sh-1`, `sh-2`, pruned and disposed on exit.
- [X] T050 Add `wait_seconds` to `run_shell`: block up to the cap, then hand back the handle with a `running: sh-N` line instead of killing the command.
- [X] T051 Stream `run_shell` output to the sink while it runs, keeping the complete text in the result the model reads.
- [X] T052 Add `shell_output` (drain since the last read, optional wait, status line) and `shell_stop` (tree kill, idempotent once the process has exited).
- [X] T053 Latch the exit code when the process is observed as exited, so `shell_output` cannot report a finished command with no code.
- [X] T054 Add `FragmentFinder` in `Domain.Text`: a four-rung ladder (exact, line endings, trailing whitespace, indentation) that returns the first non-overlapping match per block and re-indents and re-terminates the replacement.
- [X] T055 Rewrite `replace_in_file` around an `edits` array (max 20), applied in order against each intermediate state, all-or-nothing, still one temp-file-plus-move write.
- [X] T056 Label each preview block (`# block 2/4 (indentation)`) and name the rung a fuzzy match used, so a silent indentation repair is visible before approval.
- [X] T057 Register `ShellProcessRegistry` and the three shell tools in the composition root; record the new public surface in `PublicAPI.Unshipped.txt`.
- [X] T058 Tests: `FragmentFinderTests` (14), `RunShellCommandToolTests` (11), `ReplaceInFileToolTests` (15), `ConsoleRenderingTests` (28), `CodingAgentTests` (17), `PromptLibraryTests` (8), which pins the two new operating rules.
- [X] T059 Tell the model both affordances in `PromptLibrary.SystemPrompt` — batch fragments into one `edits` array, and poll a command that outlived its `wait_seconds` with `shell_output` — and document them in `README.md`.
- [X] T060 `ToolResultTrimmer` now reserves room for its own marker and count line, so a trimmed result fits the budget it was given — found while sizing the larger shell and edit results. Pinned by `ToolResultTrimmerTests`.

## Found in passing

- [X] T045 Fix `AgentSession.RecordUsage`, which seeded a model's first row with the reported tokens and then added them again, so `/usage` reported double the first turn and `/usage` disagreed with its own total. Pinned by `AgentSessionUsageTests`.

## Verification

- [X] T041 `dotnet build -c Release` clean.
- [X] T042 `dotnet test -c Release` green: 576 tests, 0 failed.
- [X] T043 Redirected smoke test: `models` and `--help` contain no escape byte.
- [X] T044 `run --provider demo --mode plan` completes without a write.
