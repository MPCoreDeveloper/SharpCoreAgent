# Recorded coding-task evaluations

Run from the repository root:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --check
```

The corpus is intentionally small and deterministic:

- `csharp-division`: fragment edit, production `dotnet_verify`, then independent execution of the fixed calculation.
- `javascript-discount`: fragment edit and visible `node:test`, followed by two boundary tests added after the turn.
- `python-cart`: fragment edit and visible `unittest`, followed by empty-cart and decimal-price tests added after the turn.

`baseline.json` is the accepted semantic comparison document. `artifacts/evals/` holds current reports and is ignored.
Timestamps, durations and command text are diagnostic, not comparison inputs. The checked-in baseline records the authoring
environment's outcomes; it is not a live-model benchmark or a promise that optional Python/Node runtimes exist everywhere.

## Live mode

The recorded corpus is also available as an explicit live-provider run:

```powershell
$env:DEEPSEEK_API_KEY = '<key>'
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --live --provider deepseek
```

Live mode uses the product's `IChatModelResolver`, writes `artifacts/evals/live.json` by default, and cannot be combined
with `--check`. `--provider`, `--model`, `--base-url`, `--credential-file`, `--max-iterations`, `--max-output-tokens`
and `--max-total-tokens` bound and select the run. A missing key fails before the first request with exit code 2. Live
results are human-reviewed observations: different iterations, tool order, prose and token counts are expected, and
`--check` remains the deterministic recorded gate. See
[`../../specs/039-live-model-evaluation/quickstart.md`](../../specs/039-live-model-evaluation/quickstart.md).

See [`../../specs/033-agent-evaluation/quickstart.md`](../../specs/033-agent-evaluation/quickstart.md) for the recorded
options and the explicit baseline-update procedure, and
[`contracts/evaluation.md`](../../specs/033-agent-evaluation/contracts/evaluation.md) for scenario authoring.
