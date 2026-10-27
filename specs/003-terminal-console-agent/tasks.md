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
- [ ] T036 Re-verify `deepseek-reasoner` tool-call support against the live API and flip `SupportsTools` if it changed.
- [ ] T037 Refresh both catalogues against the providers' current model lists, with a test that pins the identifiers.
- [ ] T038 Add `/cost` backed by per-model prices, or state in the help that no price is known.
- [ ] T039 Add an explicit fallback chain for 401/5xx (`--fallback grok:grok-4`) that records both attempts in the report.

## Phase 5 — MCP

- [ ] T040 Opt-in MCP client (`--mcp <command>`), tools registered through the same `IToolRegistry`, approval inherited from `ToolSafety`.

## Found in passing

- [X] T045 Fix `AgentSession.RecordUsage`, which seeded a model's first row with the reported tokens and then added them again, so `/usage` reported double the first turn and `/usage` disagreed with its own total. Pinned by `AgentSessionUsageTests`.

## Verification

- [X] T041 `dotnet build -c Release` clean.
- [X] T042 `dotnet test -c Release` green: 500 tests, 0 failed.
- [X] T043 Redirected smoke test: `models` and `--help` contain no escape byte.
- [X] T044 `run --provider demo --mode plan` completes without a write.
