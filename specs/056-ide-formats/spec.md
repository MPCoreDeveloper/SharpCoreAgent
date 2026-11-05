# Feature Specification: IDE formats

**Feature**: `056-ide-formats`
**Status**: implemented 2026-11-04

## What ships

- `.vscode/tasks.json` version `2.0.0` with `type: "shell"`, `group: { "kind": "build", "isDefault": true }` and
  `problemMatcher: "$msCompile"`.
- `.github/agents/sharpcoreagent.agent.md` with YAML frontmatter:
  - `name: sharpcoreagent`
  - `description: ...`
  - `tools: [...]` listing the five published MCP tools
  - an instruction body and marker.
- `model` is optional and deliberately omitted so the developer's editor chooses; `mcp-servers` is omitted because IDE
  agents do not use it.

## Requirements

- **FR-001**: The task names `type: shell`, because the command and argument list are a shell task.
- **FR-002**: The task is the default build group (`kind: build`, `isDefault: true`).
- **FR-003**: The task uses `$msCompile`.
- **FR-004**: The agent file lives under `.github/agents/` with a Markdown body and YAML frontmatter.
- **FR-005**: `name` and `description` are present; `tools` is present; `model` is optional.
- **FR-006**: Existing write/merge/refusal behaviour and the marker rule are unchanged.

## Non-goals

- Writing `CLAUDE.md`, `AGENTS.md`, prompt files, instruction files or skills.
- A `--force` overwrite of somebody else's file.
- A VS Code extension.

## Success criteria

- `ide tasks` prints the new keys.
- `ide agent` prints the name line before description and tools.
- Full suite, build and evaluation green.
