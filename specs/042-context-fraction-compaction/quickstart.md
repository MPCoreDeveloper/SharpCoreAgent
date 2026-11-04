# Quickstart: context-fraction automatic compaction

By default, the loop compacts before a request when its estimated prompt exceeds 80% of the model's advertised context
window. No flag is needed for the default.

```powershell
# Compact earlier, at half the window.
sharpcoreagent chat --compact-at 0.5

# Disable the size-aware trigger; the message-count limit and /compact still work.
sharpcoreagent chat --compact-at 0
```

The estimate includes message text, tool calls and their arguments, image base64, and the definitions of every
advertised tool. It is an estimate, not a provider bill.

When the trigger fires, the loop keeps the system prompts and the newest message, drops older history through
`AgentSession.Compact`, and emits the usual `ContextCompacted` notice. A tool result is never separated from the call
that produced it; if no safe cut exists, the request is sent unchanged.

`--compact-at` applies to `run` and `chat`. The existing `/compact [n]` command remains the manual door.

See [`contracts/compaction.md`](contracts/compaction.md) for the option and event contract.
