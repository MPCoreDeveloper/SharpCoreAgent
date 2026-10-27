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

## D6 — Re-read on 2026-10-27: the table is current, the defaults are not

Both pages were read again while Phase 2 was being finished, to check the three rows a test pins:

| Row in `ModelPricing` | Page says | Verdict |
|---|---|---|
| `deepseek:deepseek-flash` 0.30 / 1.20 | peak cache-miss $0.30 in, peak output $1.20 (off-peak $0.15 / $0.60) | unchanged |
| `deepseek:deepseek-v4-pro` 1.32 / 3.96 | peak cache-miss $1.32 in, peak output $3.96 (off-peak $0.66 / $1.98) | unchanged |
| `grok:grok-4.7` 2.00 / 6.00 | $2.00 in, $6.00 out, 500k context, agentic tool calling | unchanged |

So the as-of date, the currency and the sources stand. One sentence from the DeepSeek page is worth
recording because it bears on T015: the footnote says the retired legacy names `deepseek-v4-flash` and
`deepseek-v4-flash-vision-exp` are "still accepted, but the corresponding models have been retired,
their requests are served by the DeepSeek-V4.1-Flash-1017 model and billed at the Flash price". The
provider redirects a retired identifier rather than rejecting it — for those names, at least. The two
identifiers this build still ships as defaults, `deepseek-chat` and `grok-4`, appear on neither page,
and nothing on either page promises a redirect for them.

**Re-checked live on 2026-10-27, once a `DEEPSEEK_API_KEY` was in place.** The page says nothing about
the two identifiers this build used to default to, so they were asked directly rather than read about.
`deepseek-chat` and `deepseek-reasoner` both answer `200`, and the response body reports
`"model": "deepseek-flash"`: the provider resolves them as aliases of Flash and bills Flash's price. An
identifier that exists not at all (`deepseek-nonsense-xyz`) is rejected with `400` — "The supported API
model names are deepseek-flash, deepseek-v4-pro" — so the redirect is a deliberate alias, not a
catch-all. Two consequences. The T015 move stands: a default that names an alias is worse than one that
names the model the alias resolves to. But the framing above of the old default as "a first-run failure
waiting for the developer who types nothing" was too strong; `deepseek-chat` still answers, it just no
longer names a model of its own. And `deepseek-reasoner` returned a `tool_calls` payload when asked to
call a tool — because what answered was Flash, which is the model its `SupportsTools` flag is now
describing; that flag is the open question in 003 T036.

**T016 decision — the `openai-compatible` default stays `gpt-4o-mini`.** That provider is an endpoint,
not a vendor: its model identifier is whatever the server behind `--base-url` serves, so no default
this build could ship would be right more often than this placeholder, and `--model` is the answer.
The pricing half of the question was settled in D3: it stays unpriced, so `/cost` prints
`no price known` rather than a rate nobody published.

**T015 decision — both defaults move.** `DeepSeekChatModel.DefaultModelId` becomes `deepseek-flash` and
`GrokChatModel.DefaultModelId` becomes `grok-4.7`, because a default that names a model its provider no
longer lists is a first-run failure waiting for the developer who types nothing. The resume story is
what makes the move safe: `AgentSession.FromSnapshot` resolves the transcript's own `provider` +
`modelId` through `ModelCatalog.DescriptorFor`, which accepts any identifier and prefers the catalogue
entry, and the three older Grok identifiers and the two older DeepSeek identifiers stay listed so a
resumed conversation still opens on the model it was using. `ModelCatalogTests` pins both halves — the
defaults are today's models, and every retired identifier still resolves — and the catalogue lists each
provider's default first, so the model a run would pick is the one `models` prints first. The four
alias rows in `ColorAndModeOptionTests` (`ds`, `grok`, `g4`) follow the defaults; `r1`/`reasoner` and
`g3` keep naming what they named before.


## D5 — Why not put the price on `ModelDescriptor`

The obvious shape is `ModelDescriptor.Price`. Rejected for two reasons:

- `ModelDescriptor` is a positional record in `Domain`; a new member is a change to the checked public
  API, and a synthesised descriptor for an unlisted model (`DescriptorFor` on a model released after
  this build) would need a price field it cannot fill.
- Pricing which lives beside the descriptor tempts callers to invent one. A separate table makes
  "absent" the default state, which is the behaviour the requirements demand.
