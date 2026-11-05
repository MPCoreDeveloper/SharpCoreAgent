# Feature Specification: Browser automation

**Feature**: `052-browser-automation`
**Status**: implemented 2026-11-04

## Problem

`010` T031 deliberately required `--mcp "npx -y @playwright/mcp@latest"`. That left every developer to discover the
command, the channel and the tool names. Codex and Claude expose a browser capability directly; this round makes the
same official Playwright MCP server a first-class option without shipping a browser or a second driver.

## What ships

- `--browser` on `run` and `chat`.
- On Windows it adds `npx -y @playwright/mcp@latest --browser msedge`.
- Elsewhere it adds `npx -y @playwright/mcp@latest --browser chromium`.
- The command is appended to the invocation's MCP server list, so the existing client performs the handshake, names
  the tools `npx_browser_*`, enforces MCP safety and kills the child at exit.
- `--browser` is refused on commands that never run a turn, and it is not a settings key or environment variable.
- A developer who needs Firefox, WebKit, a different channel or flags can still use `--mcp` with the full command.

## Functional requirements

- **FR-001**: `--browser` sets one boolean and adds exactly one command.
- **FR-002**: The command uses the installed Edge on Windows and Chromium elsewhere; no browser download is required
  by this feature.
- **FR-003**: The browser server is a normal MCP server: tools are `npx_browser_*`, startup failure is exit 2, and
  the process tree is killed with the session.
- **FR-004**: `--browser` is accepted on `run`/`chat` only; other commands refuse it with the MCP sentence.
- **FR-005**: The flag is not persisted and has no environment variable.
- **FR-006**: `--browser` and `--mcp` compose; explicit servers keep their order and the browser is appended.
- **FR-007**: The prompt's MCP rule appears because the resulting `SharpCoreAgentOptions.McpServers` is non-empty.

## Non-goals

- Bundling Chromium, Playwright or a driver installer.
- A native `browser` tool that bypasses MCP.
- Cookie/login policy of its own; the Playwright server owns the profile.
- PDF/video/audio artefacts (`010` T032).

## Success criteria

- Parsing `--browser` yields `Browser=true` and a `ToSharpCoreAgentOptions().McpServers` entry ending in the
  platform channel.
- `models`, `index`, `config` and `setup` refuse it.
- `npx -y @playwright/mcp@latest --version` reports 0.0.84 on this machine (measured 2026-11-04).
- Full suite, build and recorded evaluation remain green.
