# Contract: Session Format

**File**: `.sharpcoreagent/sessions/<timestamp>.json` by default; `.sharpcoreagent/` is created on first
save and is listed in `.gitignore`. Encoding UTF-8 without BOM, newline `\n`, two-space indent,
property order as documented (source-generated `JsonSerializerContext`, `WriteIndented = true`).

## Document

```json
{
  "version": 1,
  "workspaceRoot": "d:\\repos\\MPCoreDeveloper\\SharpCoreAgent",
  "provider": "deepseek",
  "modelId": "deepseek-chat",
  "approval": "Ask",
  "createdAtUtc": "2026-10-23T10:15:02.184Z",
  "updatedAtUtc": "2026-10-23T10:16:41.002Z",
  "messages": [
    { "role": "User", "content": "add a README section about the CLI" },
    { "role": "Assistant", "content": "", "toolCalls": [
        { "id": "call_0", "name": "read_file", "argumentsJson": "{\"path\":\"README.md\"}" } ] },
    { "role": "Tool", "content": "  1│# SharpCoreAgent", "toolCallId": "call_0", "toolName": "read_file", "isError": false },
    { "role": "Assistant", "content": "I added the section." }
  ],
  "usage": [
    { "modelId": "deepseek:deepseek-chat", "inputTokens": 1842, "outputTokens": 311, "turns": 2 },
    { "modelId": "grok:grok-4", "inputTokens": null, "outputTokens": null, "turns": 1 }
  ]
}
```

## Rules

1. `version` is `1`; a reader refuses an unknown major version with a message naming the file.
2. `null` token counts mean **unknown**, never zero — a provider that omits usage must not be
   recorded as free.
3. `messages` is append-only within a turn and ordered; the last message of a completed turn has
   `role: "Assistant"` and no `toolCalls`.
4. `toolCalls[].argumentsJson` is the raw provider string, so a replay of a saved session shows
   exactly what the model asked for.
5. **Never present**: API keys, `Authorization` headers, environment values. A grep of a saved
   session for the configured key must find nothing (asserted by `SessionStoreTests`).
6. Writing is atomic: write `*.json.tmp`, flush, then `File.Move(tmp, target, overwrite: true)`, so
   a crash mid-write cannot leave a half transcript.
7. `updatedAtUtc` is the only field that changes on a save with no new turn; a re-save of an
   unchanged session is byte-identical apart from it.
8. A resumed session (`--session <existing>`) loads `messages` and `usage`, keeps the file, and
   continues appending; `workspaceRoot` in the file is informational — the CLI's `--workspace`
   wins and a mismatch prints a warning.
9. Interrupted turns are saved with the partial assistant text and no `toolCalls`, so the transcript
   never claims a completed turn that did not complete.
