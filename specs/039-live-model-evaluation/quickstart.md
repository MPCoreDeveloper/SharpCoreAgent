# Quickstart: Live-model evaluation

Recorded mode is still the default and needs no credential:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --check
```

Live mode is explicit:

```powershell
$env:DEEPSEEK_API_KEY = '<your key>'
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --live --provider deepseek
```

Choose a model and bound the cost:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- `
  --live --provider deepseek --model deepseek-flash `
  --max-iterations 24 --max-output-tokens 4000 --max-total-tokens 100000
```

Use another provider, or an OpenAI-compatible endpoint:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- `
  --live --provider grok --model grok-4.7

dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- `
  --live --provider openai-compatible --model my-model `
  --base-url https://example.invalid/v1 --credential-file .\keys.txt
```

The default live report is `artifacts/evals/live.json`. `--check` cannot be combined with `--live`; compare live
results by reviewing `status`, `failures`, `unexpectedChanges` and `gradeResult`, not by requiring an identical
transcript. A live run never writes `eng/evals/baseline.json`.

A missing provider key is reported before the first request:

```text
error: live evaluation needs one of DEEPSEEK_API_KEY for provider 'deepseek'
```

See [`contracts/live-evaluation.md`](contracts/live-evaluation.md) for the exact option and report contract.
