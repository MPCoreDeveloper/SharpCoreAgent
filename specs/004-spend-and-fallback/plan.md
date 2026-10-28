# Plan — 004 spend and fallback

## Phase 1 — `/cost`

### The table

`ModelPrice` is a record of what one model costs, keyed by the pair the CLI already uses:

```csharp
public sealed record ModelPrice(
    string Provider, string ModelId, decimal InputPerMillion, decimal OutputPerMillion, string Source);
```

`Cost(int? inputTokens, int? outputTokens)` multiplies and divides by a million, and returns `null`
when either half is unknown. `Source` is the page the figures were read from — not a comment, because
a comment does not survive a copy into another file and a test can assert a field.

`ModelPricing` holds the table, one as-of date for the whole table (`DateOnly(2026, 10, 27)`), the
currency (`USD`, printed with every report because a bare `$` is an assumption), and lookups keyed by
the `provider:model` name the session records. Lookups are ordinal: `deepseek:DeepSeek-Chat` is not a
model this build prices, and the report says so rather than guessing.

Decimal, not double. The cheapest row is $0.003 per million tokens; `double` would round a small
session to something other than the product a person computes on paper.

### The report

`CostReport.Create(IReadOnlyList<ModelUsage>)` turns the session's rows into:

- `Lines` — one per model, in the same order `/usage` prints, each carrying its tokens, its price (or
  none) and its cost (or none);
- `Total` — the sum of the lines whose cost is known;
- `Unpriced` and `UnknownUsage` — the two reasons a line has no cost, counted separately because they
  are different problems with different fixes (a missing price versus a provider that reported
  nothing);
- `IsComplete` — true only when both counts are zero, which is what decides whether the total is
  printed as a measurement or as a floor.

Three states, three renderings, and no state that silently reads as zero:

| State | Line |
|---|---|
| priced, measured | `deepseek:deepseek-flash  1000 in / 2000 out  $0.0027` |
| priced, usage unknown | `…  cost unknown` |
| unpriced | `…  no price known` |

### The command

`/cost` sits in `ChatCommand.HandleCommand` beside `/usage` and shares its guards: no usage yet is a
sentence, not an error. Rendering uses `AnsiTheme` like every other command line, so `--color never`
and `NO_COLOR` keep working without a branch.

### The catalogue

Three entries are added so the table prices something a developer can actually select today:
`deepseek-flash` and `deepseek-v4-pro` (1M context, tools, thinking by default) and `grok-4.7` (500k,
tools). `DeepSeekChatModel.DefaultModelId` and `GrokChatModel.DefaultModelId` are **not** changed: a
default model identifier is written into every transcript and every session resume path, and moving
it is a decision for the repository owner, recorded as a task rather than made in passing.

### Tests

`ModelPricingTests` pins the table: the as-of date, every rate, the sources, no duplicate keys, and
that every priced model is one `ModelCatalog` can name. `CostReportTests` pins the arithmetic and the
three states, including that unknown usage never yields a number and that the total is the sum of the
priced, measured lines only.

## Phase 2 — fallback

`--fallback <provider>:<model>` is parsed into a `ModelDescriptor` by `CliOptions`, so an unknown
provider is a usage error naming the providers this build serves, not a surprise after the first 401.
The descriptor travels through `SharpCoreAgentOptions.Fallback` to `ChatModelResolver`, which wraps the
retry-wrapped primary in `FallbackChatModel`:

```
FallbackChatModel( RetryingChatModel(primary adapter), RetryingChatModel(fallback adapter) )
```

Outside the retry decorator, because a provider retrying itself is not a fallback and a report must not
confuse the two. The CLI checks the fallback's credential at startup beside the primary's: a fallback
whose key is missing cannot cover the failure it exists for.

### What is replayed

Only `ModelRequestException` with status 401 or a 5xx, and only while nothing has been yielded. The
replay is `request with { Model = fallback.Model }`: the identifier is the one field that has to change,
because it is what the endpoint is asked to serve. Messages, tools, temperature and the output ceiling
travel unchanged — a fallback is the same turn on a different provider, not a different turn.

A 400 (or 422, or 429) is surfaced rather than replayed: the first says the request is malformed and the
second would meet the same answer twice. A dropped connection carries no status and is the retry
decorator's problem, which gets another go at the same provider before anyone else is asked.

### What the report says

The decorator emits `FallbackUsed(primary, status, reason, fallback)` ahead of the fallback's first
increment — ahead on purpose, so a fallback that also fails still leaves the first attempt on the
record. `CodingAgent` turns it into three things:

- a `FallbackAttempted` event, which the console prints when it happens, on stderr, with the provider's
  own message (already stripped of any credential by the adapter);
- an entry in `AgentReport.Fallbacks`, and in the report's JSON;
- the turn's *serving* model, which is what `AgentSession.RecordUsage` is keyed by and what
  `AgentReport.ModelId` reports. So `/cost` bills the fallback's tokens at the fallback's prices, which
  is the only attribution that is not a guess.

Without `--fallback` none of this exists: the resolver returns the retry-wrapped primary exactly as
before, and every report carries an empty `Fallbacks`.


## Phase 3 — catalogue decisions

`openai-compatible` keeps `gpt-4o-mini` as its default (T016): the provider is an endpoint, so the
identifier belongs to whoever runs it and `--model` is how it is chosen; the model stays unpriced, so
`/cost` says `no price known` rather than quoting a rate nobody published.

The table was re-read on 2026-10-27 (T017). All three rows, the as-of date and the two sources are
unchanged, and both pages now list exactly the three models the table prices — which is what makes
the remaining question (T015) sharp: `deepseek-chat` and `grok-4` are still this build's defaults and
appear on neither page.

The defaults moved (T015, on the owner's decision): `deepseek-flash` and `grok-4.7` now answer a run
that names no model, and each provider's catalogue leads with its default so `models` prints the model
a run would pick first. The resume story is what makes the move safe and it was already in place: a
transcript names its own provider and model, `ModelCatalog.DescriptorFor` resolves any identifier, and
the five retired identifiers stay listed so a resumed conversation still opens where it left off.
`ModelCatalogTests` pins both halves — today's defaults, and every retired identifier still resolving —
so a future move cannot quietly strand a transcript.
