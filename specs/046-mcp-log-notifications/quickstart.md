# Quickstart: MCP logging notifications

Run a server with verbose logging:

```powershell
sharpcoreagent chat --verbose --mcp "npx -y some-server"
```

When the server sends:

```json
{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"warning","logger":"test","data":"careful"}}
```

the diagnostics stream carries:

```text
server log [warning] test: careful
```

The line is capped at 500 characters and is never sent to the model or written to the transcript.
