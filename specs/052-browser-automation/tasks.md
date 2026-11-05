# Tasks: Browser automation

- [x] T001 Add `CliOptions.Browser` and the platform-specific Playwright command.
- [x] T002 Append it to `McpServers` in `ToSharpCoreAgentOptions`; start MCP when only `--browser` is set.
- [x] T003 Refuse `--browser` on non-turn commands and document the `ide` case.
- [x] T004 Tests for run/chat, refusal, ordering and the command string.
- [x] T005 Help, README, `010` T031 and the roadmap.
- [x] T006 Build, full suite and recorded evaluation.

## Evidence

`TheBrowserFlagStartsThePlaywrightServer`, `TheBrowserFlagIsRefusedWhereNoTurnRuns`,
`TheBrowserFlagAndAnExplicitServerStayInOrder`, and `npx -y @playwright/mcp@latest --version` → `0.0.84`.

## Out of scope

Bundled browser/driver, native browser tool, profile policy, PDF/video/audio artefacts.
