# Tasks: Live model-catalogue refresh

- [x] T001 Query DeepSeek `/models` without printing the key.
- [x] T002 Query Grok `/models` without printing the key.
- [x] T003 Update DeepSeek context windows and `deepseek-flash` image capability.
- [x] T004 Add the live Grok text/chat identifiers in display order.
- [x] T005 Add exact identifier and context-window catalogue tests.
- [x] T006 Update the image-capability test for DeepSeek Flash.
- [x] T007 Run Release build, full suite and recorded evaluation.

## Evidence

- DeepSeek: 2 model IDs; flash `1,048,576` and text+image; v4-pro `1,048,576` text.
- Grok: 13 IDs returned; 8 text/chat IDs added, 3 existing retained, image/video generation IDs excluded.
- `ModelCatalogTests` pins the 4 DeepSeek and 11 Grok entries plus the six new context windows.
- `deepseek-flash` now declares `SupportsImages`; `deepseek-v4-pro`, the retired DeepSeek aliases and `grok-3-mini`
  remain text-only.

## Out of scope

Runtime `/models` polling, new pricing rows, default changes, image/video generation models, and network access from CI.
