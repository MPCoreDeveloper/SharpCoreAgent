# Quickstart: Browser automation

```powershell
sharpcoreagent chat --browser
```

This is equivalent to:

```powershell
sharpcoreagent chat --mcp "npx -y @playwright/mcp@latest --browser msedge"
```

on Windows, and to `--browser chromium` elsewhere. The tools arrive as `npx_browser_navigate`,
`npx_browser_snapshot`, `npx_browser_click`, `npx_browser_type`, `npx_browser_take_screenshot` and the rest of the
Playwright MCP catalogue. Screenshots land in `.sharpcoreagent/artifacts/` through the existing sink.

Use `--mcp` instead when you need a different channel or extra server flags. `--browser` is per invocation and is not
written to the settings file.
