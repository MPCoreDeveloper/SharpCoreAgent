# Quickstart: spend and fallback

Runnable validation for `004-spend-and-fallback`. Steps 1 and 4 run offline on the demo provider with no
credential set; steps 2, 3 and 5 send one request to DeepSeek, and 3 and 5 deliberately send an invalid
key so the refusal is the point.

## 1. `/cost` says what it cannot know

```powershell
$ws = Join-Path $env:TEMP 'cost-demo'; New-Item -ItemType Directory -Force $ws | Out-Null
'hello','/cost','/exit' | dotnet run --project src\SharpCoreAgent.Cli -- chat --provider demo --workspace $ws --no-index
```

Expected, in order: the demo's answer; then a first line naming the table —
`prices as of 2026-10-27, USD per million tokens; the peak rate is used where the provider publishes
tiers` — then `demo:demo-scripted  356 in / 29 out  no price known`, and a total reading
`unknown: no line could be priced and measured  partial: 1 model(s) unpriced, 0 without reported
usage`.

The number never appears as `$0.00`: a demo turn has no published price, and `$0.00` would be read as
a measurement.

## 2. A priced turn matches a hand computation

```powershell
# LIVE: needs DEEPSEEK_API_KEY and spends a fraction of a cent
'list the files in this repository','/cost','/exit' | dotnet run --project src\SharpCoreAgent.Cli -- chat --provider deepseek --model deepseek-flash
```

Take the two numbers the line prints and the row the table holds for `deepseek:deepseek-flash`
(`0.30` in and `1.20` out per million, peak cache-miss — `src/SharpCoreAgent.Infrastructure/Providers/
ModelPricing.cs`), and check the product by hand:

```
cost = input / 1_000_000 * 0.30 + output / 1_000_000 * 1.20
```

Expected: the printed amount equals that product. It is computed in `decimal`, so a small session
prints as e.g. `$0.0021` rather than something a calculator disagrees with, and the total adds the
priced, measured lines only.

## 3. A refused turn falls back once

```powershell
$env:DEEPSEEK_API_KEY = 'sk-invalid-000000000000000000000000'
dotnet run --project src\SharpCoreAgent.Cli -- run "say hi" --provider deepseek --model deepseek-chat `
  --fallback demo:demo-scripted --approval readonly --no-session --no-index
$LASTEXITCODE   # 0 — the fallback answered
```

**LIVE for the first attempt only**: DeepSeek rejects the fabricated key with 401, which is exactly the
case the fallback exists for. Expected on stderr:

```
fallback: deepseek:deepseek-chat failed with HTTP 401; demo:demo-scripted answered deepseek:deepseek-chat returned HTTP 401: … api key: ****0000 is invalid …
```

Note what the provider itself printed back: the key is redacted (`****0000`), because the adapter
sanitises the message before it reaches the terminal or the report.

## 4. A fallback that cannot start is refused before the turn

```powershell
Remove-Item Env:\XAI_API_KEY, Env:\GROK_API_KEY -ErrorAction SilentlyContinue
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider demo --fallback grok:grok-4.7
$LASTEXITCODE   # 3
```

Expected: `error: grok needs a credential and none is set for grok:grok-4.7. Set XAI_API_KEY or
GROK_API_KEY and try again.` — the session never starts, because a fallback that cannot be built is
worse than no fallback.

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- chat --fallback openai:gpt-4o-mini
$LASTEXITCODE   # 2
```

Expected: `'openai:gpt-4o-mini' is not a fallback this build can use.` plus a hint listing the four
provider keys.

## 5. `/cost` bills the model that answered

```powershell
$env:DEEPSEEK_API_KEY = 'sk-invalid-000000000000000000000000'
'say hi','/cost','/exit' | dotnet run --project src\SharpCoreAgent.Cli -- chat --provider deepseek `
  --model deepseek-chat --fallback demo:demo-scripted --no-session --no-index
```

Expected: the fallback line, the demo's answer, and a `/cost` whose only model is
`demo:demo-scripted` — the model that produced the tokens — never `deepseek:deepseek-chat`, which
produced none.

## Automated equivalent

```powershell
dotnet test .\SharpCoreAgent.slnx -c Release
```

`FallbackChatModelTests` pins 401/5xx fall back, 400/422/429 and a failure after a delta do not, the
replay carries the fallback descriptor with the rest of the request unchanged, and a fallback that also
fails still names both attempts. `ModelPricingTests` pins the rates, the as-of date and every source.
