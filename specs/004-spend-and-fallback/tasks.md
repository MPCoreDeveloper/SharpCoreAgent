# Tasks — 004 spend and fallback

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the
next pass has a real list rather than a memory.

## Phase 1 — `/cost`

- [X] T001 Add `ModelPrice` (`Provider`, `ModelId`, `InputPerMillion`, `OutputPerMillion`, `Source`) with `QualifiedName` and `Cost(int?, int?)`, returning `null` when either half is unknown.
- [X] T002 Add `ModelPricing`: one as-of date, `USD`, the table, and ordinal lookups by `provider:model`.
- [X] T003 Add `CostReport` (`Lines`, `Total`, `Unpriced`, `UnknownUsage`, `IsComplete`) over `IReadOnlyList<ModelUsage>`.
- [X] T004 Add `/cost` to the interactive loop, rendered through `AnsiTheme`, with the as-of line first and a partial total when any line is unpriced or unmeasured.
- [X] T005 Add `deepseek-flash` and `deepseek-v4-pro` to the DeepSeek catalogue (1M context, tools, thinking), leaving `DefaultModelId` alone.
- [X] T006 Add `grok-4.7` to the Grok catalogue (500k context, tools), leaving the three older entries and their default in place.
- [X] T007 Document `/cost`, the as-of date and the peak-rate rule in `HelpText.ChatCommands`, `--help` and `README.md`; update the provider table.
- [X] T008 Tests: `ModelPricingTests` (date, rates, sources, duplicate keys, priced models are catalogue models) and `CostReportTests` (arithmetic, three states, partial total).
- [X] T009 Record the sourced figures and the peak-rate decision in `research.md`, including that DeepSeek no longer lists `deepseek-chat`/`deepseek-reasoner` and xAI no longer lists `grok-4`/`grok-3`/`grok-3-mini`.

## Phase 2 — fallback

- [ ] T010 Add `--fallback <provider>:<model>` to `CliOptions`, refusing an unknown provider at parse time.
- [ ] T011 Add `FallbackChatModel` in `Infrastructure.Chat`: replay once at the fallback for 401 and 5xx only, never after a delta has been surfaced, never for 400.
- [ ] T012 Record both attempts in the report (provider, model, status or message) without echoing a credential.
- [ ] T013 Wire the decorator in the composition root, outside `RetryingChatModel` so a retried primary is not confused with a fallback.
- [ ] T014 Tests: 401 falls back once, 5xx falls back once, 400 does not, a delta before the failure does not, and the report names both attempts.

## Phase 3 — catalogue decisions

- [ ] T015 Decide whether `DeepSeekChatModel.DefaultModelId` moves from `deepseek-chat` to `deepseek-flash`, and whether `GrokChatModel.DefaultModelId` moves from `grok-4` to `grok-4.7`; both are written into transcripts, so the change needs a resume story.
- [ ] T016 Decide whether `OpenAiCompatibleChatModel.DefaultModelId` stays `gpt-4o-mini` now that OpenAI publishes no standard price for it.
- [ ] T017 Re-read both pricing pages and refresh `ModelPricing` (rates, as-of date, sources) — the three numbers that a test currently pins.
