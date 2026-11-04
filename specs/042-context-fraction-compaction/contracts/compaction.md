# Contract: context-fraction compaction

| Item | Contract |
|---|---|
| Option | `--compact-at <fraction>` on `run` and `chat` |
| Default | `0.8` |
| Disable | `0` |
| Valid non-zero range | `0.1`–`1.0` |
| Trigger | estimated prompt tokens > `floor(contextWindow * fraction)` |
| Estimate inputs | message text, tool-call names/arguments, image base64, tool name/description/schema |
| Action | `AgentSession.Compact(1)` before the request |
| Event | one `ContextCompacted(Dropped, Kept)` after all cuts |
| Unknown window | size trigger skipped; count trigger still applies |
| No safe cut | stop; send the request unchanged |

The .NET setting is `AgentOptions.CompactAtContextFraction`; the host setting is
`SharpCoreAgentOptions.CompactAtContextFraction`. The CLI maps `--compact-at` to both.

## Observable behavior

- A transcript with large history on a small-window model is reduced before the model call.
- A transcript below the threshold is byte-for-byte untouched by this trigger.
- `ContextCompacted` is emitted once per request, not once per internal cut.
- The existing `/compact` and `MaxContextMessages` paths are unchanged.

## Limits

The estimator uses four characters per token. It over-counts images (base64 length) and does not model provider-specific
chat formatting. It is deliberately only a safety trigger: usage in reports still comes from the provider.
