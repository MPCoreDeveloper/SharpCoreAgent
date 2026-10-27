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

## Phase 2 — fallback (planned, not built)

`FallbackChatModel` decorates the resolved model and replays the request once at the fallback when the
primary throws a `SharpCoreAgentException` whose status is 401 or 5xx. The report gains both attempts.
The retry happens before any delta is surfaced (the rule `RetryingChatModel` already follows), so a
turn that has started streaming is never silently restarted.

## Phase 3 — catalogue decisions (planned, not built)

Whether the default model identifiers move to the current generation, and whether `gpt-4o-mini` stays
the `openai-compatible` default given that OpenAI no longer publishes a price for it.
