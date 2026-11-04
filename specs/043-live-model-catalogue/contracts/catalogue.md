# Contract: model-catalogue refresh

| Provider | Checked-in text/chat IDs |
|---|---|
| DeepSeek | `deepseek-flash`, `deepseek-v4-pro`, `deepseek-chat`*, `deepseek-reasoner`* |
| Grok | `grok-4.7`, `grok-4.6`, `grok-4.5`, `grok-4.3`, `grok-4.20-0309-non-reasoning`, `grok-4.20-0309-reasoning`, `grok-4.20-multi-agent-0309`, `grok-build-0.1`, `grok-4`*, `grok-3`*, `grok-3-mini`* |

`*` Retained for old transcripts; not necessarily advertised by the provider today.

## Capabilities

- `deepseek-flash`: 1,048,576 context, image input.
- `deepseek-v4-pro`: 1,048,576 context, text only.
- Grok 4.3/4.20: 1,000,000 context, image input.
- Grok 4.5/4.6/4.7: 500,000 context, image input.
- Grok Build 0.1: 256,000 context, image input.
- Grok 3 Mini: 131,072 context, text only.

## Rules

- Defaults are unchanged: `deepseek-flash` and `grok-4.7`.
- Retired IDs stay; unknown IDs synthesize a descriptor.
- No pricing is inferred here; unpriced models report `no price known`.
- Image/video generation IDs are not chat catalogue entries.
- The live probe is a maintainer action; CI uses the pinned tests only.
