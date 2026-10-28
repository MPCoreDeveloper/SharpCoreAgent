# 004 — Spend and fallback

**Status**: Phase 1 (`/cost`) and Phase 2 (fallback) implemented; the Phase 3 catalogue decisions are
listed in `tasks.md`
**Branch**: `feature/sharpcoredb-2-1-2`
**Depends on**: `001-coding-agent-core`, `003-terminal-console-agent`

## Why this feature exists

`/usage` answers "how many tokens". The question a developer about to start a forty-turn session
actually asks is "what will this cost me", and `003` answered it by not answering it: the spec's out
of scope list names per-token cost in currency and provider fallback on 401/5xx as deliberately
deferred. Two things made the deferral stop being free.

1. **The prices stopped being a constant.** DeepSeek now publishes two tiers — off-peak and peak,
   with peak at exactly double — and its model list no longer contains the two identifiers this build
   ships as defaults. xAI's flagship is `grok-4.7` at 500k context; `grok-4`, `grok-3` and
   `grok-3-mini` are gone from the models page and are covered by a retirement notice. A build that
   cannot price a turn also cannot notice it is calling a retired model.
2. **The numbers were already in the transcript.** Every price is tokens multiplied by a published
   rate, and the session already accumulates exactly those token counts per model. Nothing about
   `/cost` needs a credential, a network call or a new event on the wire.

Fallback is the second half of the same story: if a provider retires the model under a running
session, the useful behaviour is a defined retry against a model the developer named up front, with
both attempts visible in the report — not a stack trace after the 401.

## User stories

### US1 — "What has this cost me so far" (P0, Phase 1)

As a developer in a long `chat`, I want `/cost` to tell me what the tokens reported so far would
cost, and to say plainly where it cannot know, so that a spend decision is made from numbers rather
than a guess.

Acceptance:

- `/cost` prints one line per model that served the session: its tokens, and its cost.
- A model with no published price prints `no price known`, never `$0.00`.
- A model whose usage the provider never reported prints `cost unknown`, never `$0.00`.
- The first line names the date the prices were read and the currency, so a stale table is visible
  rather than trusted.
- A total follows, marked partial when any line could not be priced.
- `/usage` is unchanged: it reports tokens, which is a different question with a different answer.

### US2 — A fallback that is named, not discovered (P1, Phase 2)

As a developer whose provider starts returning 401 or 5xx mid-session, I want `--fallback
provider:model` to retry the turn there and to record both attempts, so that the turn either
completes or fails with an explanation of both providers.

Acceptance:

- `--fallback grok:grok-4.7` retries a turn that failed on 401 or 5xx at the fallback.
- A 400 (a malformed request) is not retried: the same body would fail the same way.
- The report names both attempts and which one produced the answer, and neither credential is
  echoed.
- Without `--fallback` nothing changes.

## Functional requirements

- **FR-001** Prices MUST live in one table, each entry carrying the URL it was read from and a single
  table-wide as-of date.
- **FR-002** A cost MUST be unknown, not zero, when either half of the usage is unknown. A fabricated
  number is worse than a missing one, which is the rule `TokenUsage` already follows.
- **FR-003** A model absent from the table MUST report `no price known`; the report MUST NOT
  interpolate a price from a neighbouring model.
- **FR-004** `/cost` MUST print the as-of date and the currency before any number, and MUST mark the
  total partial when a line is unpriced or unmeasured.
- **FR-005** The table MUST be pinned by tests: the numbers, the as-of date, the absence of duplicate
  keys, a source for every entry, and that every priced model is one the catalogue can name.
- **FR-006** `HelpText.ChatCommands` MUST document `/cost` and the peak-rate rule, and `--help` MUST
  point at `/help` rather than repeat the list — one command list, printed in one place.
- **FR-006a** `README.md` MUST document `/cost`, the as-of date of the table and the peak-rate rule.

- **FR-007** A fallback MUST be attempted only for 401 and 5xx, MUST inherit the original request
  unchanged, and MUST appear in the report. Its tokens MUST be attributed to the model that answered,
  so the cost report prices them from the row that actually billed them.

## Success criteria

- `/cost` after a demo turn with no published price prints `no price known` and a total of `unknown` —
  never `$0.00`, which a reader would take for a measurement.
- `/cost` on a priced model matches a hand-computed product of tokens and rate to four decimals.
- `dotnet test` is green and the new tests fail if a single rate, the as-of date or a source URL is
  edited.

## Out of scope

- Currency conversion, tax, or per-request invoices; the report is USD as published.
- Cache-hit pricing. Usage does not say whether a prompt hit the cache, so the table bills the
  cache-miss rate, which is the ceiling of what the turn could have cost.
- A live price fetch. The CLI has no reason to make a network call to answer a multiplication.
- Budget enforcement by currency; `001`'s budgets are token budgets and stay that way.
- Automatic model selection on failure without a developer-named fallback.

## Risks

- **Silent drift.** Prices change and the table does not. Mitigated three ways: the as-of date is
  printed with every report, every entry carries the page it came from, and a test fails on any edit
  to a number.
- **Peak/off-peak.** DeepSeek bills peak at double off-peak. The table uses peak, so the report is an
  upper bound, and the help says so.
- **Half-known usage.** A provider that reports input but not output would make a cost too low. The
  report refuses to compute it and says `cost unknown`.
