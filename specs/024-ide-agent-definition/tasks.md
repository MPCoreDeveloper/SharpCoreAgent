# Tasks — 024 ide agent definition

**Argument**: `spec.md` (FR-001…FR-012). **Evidence**: `plan.md` (the two formats, quoted, and D1–D10).

**User story 1** — as a developer, `sharpcoreagent ide agent --write` puts an agent in my editor's picker whose tool list
is exactly this workspace's index, so I can ask my editor about my code without wiring anything by hand.

**User story 2** — as a developer, `sharpcoreagent ide tasks --write` adds one task to `.vscode/tasks.json` that rebuilds
the index with the same binary that wrote the file, so a stale index is one run away from fresh.

## Phase 1 — the shared pieces

- [X] T001 Write `spec.md`, `plan.md` and this file.
- [X] T002 `McpServerCatalog`: the six published names, public and read-only, with `McpServerPublishedSet.AllowList`
      delegating to it. The set keeps enforcing; only the array moves.
- [X] T003 `WorkspaceFileWriter` (read text, save text, no BOM, create the directory) and `WorkspaceWriteException`;
      `WriteOutcome` moves beside them and `McpConfigWriter` is re-pointed at all three. Existing behaviour and messages
      unchanged, which is what `McpConfigWriteTests` proves without being edited.
- [X] T004 `CliOptions`: `CliCommand.Ide`, `IdeAction`, `Ide`, the `McpConfigWrite` → `Write` rename, `FinishIde` and
      `RejectIdeFlags`, the `Finish` exemption for `--write`, and the widened `RejectWriteFlag` message.
- [X] T005 `HelpText`: two usage rows, the `--write` row, the `Ide` section, exit `0`'s row, two examples.

## Phase 2 — US1, the profile

- [X] T006 `AgentProfile`: the destination, the marker, the frontmatter (`description`, `tools` from
      `McpServerCatalog`), and the prompt body naming all six tools.
- [X] T007 `IdeFileWriter.WriteProfile`: created, updated, unchanged, and the refusal for a file without the marker.
- [X] T008 `IdeCommand`'s `agent` action (print, or `--write`) and the `Program` dispatch.

## Phase 3 — US2, the task

- [X] T009 `WorkspaceTasks`: the destination, the one task, and the document (`version`, `tasks`).
- [X] T010 `McpClientSnippets.Invocation(params string[])`, so the task and the MCP blocks come from one answer.
- [X] T011 `IdeFileWriter.WriteTasks`: created, updated by label, unchanged, and the two refusals.
- [X] T012 `IdeCommand`'s `tasks` action.

## Phase 4 — evidence

- [X] T013 `AgentProfileTests` `[P]`
- [X] T014 `IdeAgentWriteTests` `[P]`
- [X] T015 `IdeTasksWriteTests` `[P]`
- [X] T016 `IdeCommandParsingTests` `[P]`
- [X] T017 `HelpTextTests`: the usage rows, the `--write` row's second command, and the `0` row.

## Phase 5 — docs and record

- [X] T018 `contracts/cli.md`: the command, its two actions, its refusals and its exit codes.
- [X] T019 `README.md`: the two commands, after the `mcp config` section they extend.
- [X] T020 Run both against this repository and read the two files.
- [X] T021 `dotnet build -c Release` at 0 warnings; `dotnet test -c Release --no-build`, with the delta recorded.
- [X] T022 `specs/000-parity-roadmap.md`: round 15, and the open-items table.
- [X] T024 Read the VS Code task and custom-agent references and implement the formats. **Done in `056-ide-formats`:** `tasks.json` gains `type: "shell"`, `group: { kind: "build", isDefault: true }` and `problemMatcher: "$msCompile"`; `.github/agents/sharpcoreagent.agent.md` gains YAML `name`, `description` and `tools`, with `model` optional and omitted.
- [X] T027 Add the repository-wide instructions file. **Done:** `ide instructions` prints or writes `.github/copilot-instructions.md` with the agent body and ownership marker.
- [X] T028 Add the agent skill. **Done:** `ide skill` prints or writes `.github/skills/sharpcoreagent/SKILL.md` with YAML `name`/`description`, the agent body and ownership marker.
- [X] T029 Add `mcp-servers` to the agent profile. **Done in this slice:** `mcp-servers: [sharpcoreagent]` is part of the YAML frontmatter as a list of server names.
  `tasks.json` gains `type: "shell"`, `group: { kind: "build", isDefault: true }` and `problemMatcher: "$msCompile"`;
  `.github/agents/sharpcoreagent.agent.md` gains YAML `name`, `description` and `tools`, with `model` optional and
  omitted.

## Parallelisation

`[P]` rows are T013–T016: four files no other row writes. T017 touches a file T005 edits, so it follows.

## Implementation Strategy

### MVP first (User Story 1 only)

1. T002, T003 — the two things both actions need, with `mcp config` still green.
2. T004 — the command line, which is what makes the rest testable.
3. T006, T007, T008 — the profile, printed, then written.
4. T013, T014 — the evidence.
5. **Stop and validate**: `ide agent` in this repository, then open the written file.

### Incremental delivery

Phase 1 → `ide` exists and refuses everything it should. Phase 2 → the picker's file. Phase 3 → the task list's. Each
phase leaves a build that passes and a story that stands on its own; US2 is the JSON merge and one task on top of a
command that already writes files.

## Notes

- One slice per session: this round ended at T023. The VS Code extension remains `023`'s next round, and Visual Studio's
  own agent format was not read, so neither is a task here.
- What the round leaves open, and why it is deliberate rather than forgotten: `group`, `problemMatcher` and `type` in the
  task (D7 — the page describes them in prose this round did not read, and a guessed matcher is a file the editor rejects);
  `--force` (spec.md — a flag whose only purpose is to overwrite somebody's file); a prompt file, an instructions file and
  an agent skill (different formats, different audience); `mcp-servers` inside the profile (the reference says IDE agents
  ignore it); and Visual Studio 2026's own agent definition, which is a format and a location this round did not read.
- The two things it could not verify are in `plan.md` under "What was not verified": that VS Code resolves a
  `<server>/<tool>` entry against the names in `.vscode/mcp.json`, and that an absent `target` really does mean both
  environments. Both are documented; neither was watched in a running editor.
- The only existing code this round refactored was `McpConfigWriter`'s file handling (T003) and `McpClientSnippets.
  Invocation`'s argument list (T010). Both were re-pointed rather than rewritten, and the existing tests are the proof:
  `McpConfigWriteTests` passed unchanged through both.
- `Process.Start` appears nowhere in this round; the shell tool and the MCP client keep their two call sites.
