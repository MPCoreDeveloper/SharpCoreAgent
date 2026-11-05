# Quickstart: Artefact catalogue

```text
list_artifacts {}
list_artifacts {"limit":20}
```

The answer lists saved files newest first:

```text
artifacts: 2
.sharpcoreagent/artifacts/npx-browser_take_screenshot-2.png · 44.9 KiB · 2026-11-04 19:22:03 UTC
.sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png · 44.9 KiB · 2026-11-04 19:20:18 UTC
```

The folder is capped at 256 MiB by the MCP client; this tool only reports what is there.
