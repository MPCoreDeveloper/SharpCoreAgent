# Feature Specification: Live model-catalogue refresh

**Feature**: `043-live-model-catalogue`
**Status**: implemented 2026-11-04

## Problem

`003` T037 asks for a live re-check of both provider catalogues after the providers move. The checked-in catalogue was
last pinned on 2026-10-27; a provider can add models, change a context window, or add an input modality without changing
an identifier the build already knows. A stale catalogue silently refuses a capability (for example image input) or
hides a model the developer can select.

## What ships

- A live check of `GET https://api.deepseek.com/models` and `GET https://api.x.ai/v1/models`.
- DeepSeek `deepseek-flash` updated to the advertised 1,048,576-token window and `SupportsImages: true`;
  `deepseek-v4-pro` updated to 1,048,576 and remains text-only.
- Grok text/chat catalogue extended with the newly advertised `grok-4.6`, `grok-4.5`, `grok-4.3`, three
  `grok-4.20-0309` variants and `grok-build-0.1`.
- Grok image support declared for the new text/chat entries; `grok-3-mini` remains text-only.
- Exact identifier and context-window tests so the next live refresh is a reviewable diff.
- Retired aliases remain listed and unresolved identifiers still synthesize a descriptor.

## Live evidence (2026-11-04)

DeepSeek returned only:

```text
deepseek-flash   DeepSeek-V4.1-Flash   context 1048576   input text, image
deepseek-v4-pro  DeepSeek-V4-Pro      context 1048576   input text
```

Grok returned the text/chat IDs plus image/video generation models. The generation-only IDs
(`grok-imagine-image`, `grok-imagine-video`) are not chat tools and are deliberately not added. The advertised
text/chat set is:

```text
grok-4.7
grok-4.6
grok-4.5
grok-4.3
grok-4.20-0309-non-reasoning
grok-4.20-0309-reasoning
grok-4.20-multi-agent-0309
grok-build-0.1
```

Contexts came from the live response: 500,000 for 4.6/4.7, 1,000,000 for 4.3/4.20, and 256,000 for Build. The
existing `grok-4`, `grok-3`, `grok-3-mini` entries stay for transcript resolution.

## Functional requirements

- **FR-001**: The exact lists above are pinned by `ModelCatalogTests`.
- **FR-002**: `grok-4.7` and `deepseek-flash` remain the provider defaults; the refresh does not move a default without a
  separate decision.
- **FR-003**: New entries declare their advertised context windows and image input only where the provider page says
  Text, Image.
- **FR-004**: Retired aliases remain resolvable. No catalog entry is removed.
- **FR-005**: Unknown identifiers still synthesize a descriptor, so a transcript written after this build opens.
- **FR-006**: No pricing is invented for the new Grok models; `/cost` continues to print `no price known` until the
  separate pricing refresh (`004` T017) reads the pages.
- **FR-007**: Image/video generation model IDs are not chat tools and are not added.

## Non-goals

- Automatically calling provider `/models` at runtime; the catalogue remains a checked-in, reviewable document.
- Changing provider defaults, pricing, wire protocol, retries or fallbacks.
- Adding Grok Imagine generation/video models as chat models.
- A network test in CI; the live response is recorded here and pinned by offline tests.

## Success criteria

- Both live `/models` calls were successful without printing a credential.
- Catalogue tests pin exact IDs and context windows.
- `deepseek-flash` now permits `view_image`; `deepseek-v4-pro` and `grok-3-mini` still refuse it.
- Full suite, Release build and recorded evaluation remain green.
