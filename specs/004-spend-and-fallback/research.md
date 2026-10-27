# Research — 004 spend and fallback

Read 2026-10-27. Every figure below is quoted from the provider's own page at the URL given, because
the whole point of the feature is that the number has a source. `ModelPricing` cites the same URLs.

## D1 — DeepSeek publishes two tiers and two models

`https://api-docs.deepseek.com/quick_start/pricing`, read 2026-10-27, states prices "in units of per
1M tokens" and lists exactly two models:

| Model | Context | Tool calls | Input / 1M (cache hit, off-peak / peak) | Input / 1M (cache miss, off-peak / peak) | Output / 1M (off-peak / peak) |
|---|---|---|---|---|---|
| `deepseek-flash` | 1M | ✓ | $0.003 / $0.006 | $0.15 / $0.30 | $0.60 / $1.20 |
| `deepseek-v4-pro` | 1M | ✓ | $0.022 / $0.044 | $0.66 / $1.32 | $1.98 / $3.96 |

The page states "Off-peak rates are half of the peak rates. Peak hours are 01:00 - 04:00 and 06:00 -
10:00 UTC, Monday through Friday, excluding Chinese public holidays."

**`deepseek-chat` and `deepseek-reasoner` do not appear on this page at all.** The footnote mentions
retired legacy names (`deepseek-v4-flash`, `deepseek-v4-flash-vision-exp`) being accepted and billed
at the Flash price, but says nothing about the two identifiers this build ships as its default and
its reasoning model.

Decision: **bill the peak cache-miss rate.** Usage reports do not say whether a prompt hit the cache
and do not say which tier the request ran in, so the honest single number is the ceiling. Off-peak
turns are cheaper than the report says, never more expensive.

## D2 — xAI's catalogue has moved past this build's three entries

`https://docs.x.ai/docs/models`, read 2026-10-27, presents a single flagship:

| Model | Context | Input / 1M | Output / 1M |
|---|---|---|---|
| `grok-4.7` | 500k | $2.00 | $6.00 |

The page describes it as "our flagship model for code and everything else: agentic tool calling,
minimal hallucinations, configurable reasoning", with a knowledge cut-off of May 2026 and a
"Model Retirement (May 15, 2026)" notice in the navigation. `grok-4`, `grok-3` and `grok-3-mini` —
the three entries in `GrokChatModel.Catalog` — are not listed.

Decision: add `grok-4.7` to the catalogue with the published context and price; keep the three older
entries, because a build that cannot name a model cannot resume a transcript that used it, and a
resumed session would otherwise be refused at load time. The older entries are simply unpriced.

## D3 — OpenAI's compatible default is unpriced

`https://platform.openai.com/docs/pricing`, read 2026-10-27, lists current standard prices for
`chat-latest` ($5.00 / $30.00) and `gpt-5.3-codex` ($1.75 / $14.00) among others; `gpt-4o-mini`,
which is this build's `openai-compatible` default, is not in the current table.

Decision: leave `openai-compatible` unpriced rather than quote a retired rate. `/cost` then reports
`no price known` for it, which is true, and the developer can point the same command at a provider
that is priced. An `--base-url` endpoint is somebody else's server in any case: its prices are not
OpenAI's, and no table this build ships can know them.

## D4 — Where the table lives

`ModelPrice` and `ModelPricing` go in `SharpCoreAgent.Infrastructure.Providers`, next to the
catalogues they describe, not in `SharpCoreAgent.Domain`.

- The constitution's first rule keeps vendor facts in Infrastructure; a published rate is a vendor
  fact, and it changes on the vendor's schedule.
- `Domain` carries a checked public API (`PublicAPI.Unshipped.txt` with `RS0016`/`RS0017` as errors).
  A price is not something the domain model reasons about: the agent never makes a decision from a
  price. Putting it in Domain would add public surface with no domain behaviour behind it.

`CostReport` also lives there, because it needs `ModelUsage` (Domain) and the table, and the
`Application` project must not reference `Infrastructure` — the dependency runs the other way. The
CLI composes the two, which it already does for `ModelCatalog`.

## D5 — Why not put the price on `ModelDescriptor`

The obvious shape is `ModelDescriptor.Price`. Rejected for two reasons:

- `ModelDescriptor` is a positional record in `Domain`; a new member is a change to the checked public
  API, and a synthesised descriptor for an unlisted model (`DescriptorFor` on a model released after
  this build) would need a price field it cannot fill.
- Pricing which lives beside the descriptor tempts callers to invent one. A separate table makes
  "absent" the default state, which is the behaviour the requirements demand.
