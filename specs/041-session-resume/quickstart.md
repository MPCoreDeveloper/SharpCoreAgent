# Quickstart: resume a transcript

List the workspace's sessions without a key or a model:

```powershell
sharpcoreagent chat --resume
```

Example:

```text
  2026-11-04 12:00:00Z    7 message(s)  deepseek/deepseek-flash  .sharpcoreagent/sessions/20261104T120000Z.json
1 saved session(s) in .sharpcoreagent/sessions
resume one with: sharpcoreagent chat --resume <path>
```

Open one by the path shown:

```powershell
sharpcoreagent chat --resume .sharpcoreagent/sessions/20261104T120000Z.json
```

A relative path is resolved inside the workspace. A missing path is an error, not a new conversation:

```text
error: session '.../sessions/missing.json' does not exist; run 'sharpcoreagent chat --resume' to list saved sessions.
```

`--session <path>` is unchanged: it starts a new conversation at that path when the file is absent. Do not combine it
with `--resume`; the command rejects the ambiguity.

## Limits

The listing shows the newest first and marks a file it cannot parse as `unreadable`. It reads only
`.sharpcoreagent/sessions/*.json` in the primary workspace. It does not offer Git checkpoints, cross-process undo,
deletion or transcript editing.
