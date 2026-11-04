# Implementation Plan: Live model-catalogue refresh

**Feature**: `043-live-model-catalogue`
**Status**: implemented 2026-11-04

## Check

The maintainer check reads the provider's `/models` endpoint with the configured credential. The credential is used
only in the `Authorization` header; the output records IDs and metadata, never the key. The check is not part of CI.

## Catalogue changes

`DeepSeekChatModel.Catalog`:
- `deepseek-flash`: context `1_048_576`, `SupportsImages: true`.
- `deepseek-v4-pro`: context `1_048_576`, text-only.
- Retired aliases unchanged.

`GrokChatModel.Catalog`:
- default `grok-4.7` stays first;
- add 4.6, 4.5, 4.3, the three 4.20-0309 text variants and Build 0.1;
- keep `grok-4`, `grok-3` and `grok-3-mini` for old transcripts;
- every new text/chat entry declares images except the existing `grok-3-mini` exception.

No pricing rows are added because their pages are a separate refresh and an invented price is worse than
`no price known`.

## Verification

- Offline tests pin the exact catalogue order and each context window.
- The image capability test now checks `deepseek-flash` explicitly rather than asserting every DeepSeek entry is
  text-only.
- The existing no-duplicates and unknown-identifier tests remain unchanged.
- Full build/test/evaluation gates run after the change.
