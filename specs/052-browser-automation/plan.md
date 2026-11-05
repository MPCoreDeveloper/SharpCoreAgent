# Implementation Plan: Browser automation

## Surface

`CliOptions.Browser` is a per-invocation boolean. `BrowserServerCommand` chooses:

- Windows: `npx -y @playwright/mcp@latest --browser msedge`
- other: `npx -y @playwright/mcp@latest --browser chromium`

`ToSharpCoreAgentOptions` appends it to `McpServers` after any explicit `--mcp` entries. `CliHost.TryStartMcpAsync`
starts when either the explicit list or `Browser` is non-empty. `RejectMcpFlags` covers `--browser` on non-turn
commands, including `ide`.

## Why MCP rather than a native tool

The existing MCP client already owns startup, JSON-RPC, tool naming, safety mapping, stderr, process-tree cleanup and
the artefact sink. A native browser tool would duplicate all six and add a second driver. `--browser` is therefore a
named shortcut over the same server Codex/Claude-style browser work uses, not a second implementation.

## Tests

`McpOptionTests` pins run/chat acceptance, the refusal on four non-turn commands, and explicit `--mcp` ordering. The
server package was verified with `npx -y @playwright/mcp@latest --version` → `0.0.84`.
