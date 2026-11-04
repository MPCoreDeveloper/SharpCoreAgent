# Quickstart: Agent task evaluation

From the repository root:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release
```

The command needs the .NET SDK. Python and JavaScript scenarios report `skipped` when `python` or `node` respectively is
not available on `PATH`. No provider key is used.

The console prints each scenario and a summary. The complete report is written to `artifacts/evals/latest.json`.
A failed task prints each grading reason. Inspect its bounded tool results in that report; temporary fixture directories
are removed even after failure.

Check semantic regressions against the committed baseline:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --check
```

Run one task while diagnosing it:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --scenario csharp-division
```

After an intentional behaviour change, generate a candidate baseline and review its diff:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --output eng/evals/baseline.json
git diff -- eng/evals/baseline.json
```

Exit codes are 0 when at least one task was attempted and all attempted tasks/comparisons pass, 1 for task failure,
regression or no attempted task, 2 for bad input and 4 for cancellation.

## Interpretation

This is a deterministic **recorded workflow baseline**. It proves that the production loop can execute the recorded tool
sequence and that independent grading catches the fixture bug. Its three-for-three result is not a claim that DeepSeek,
Grok or another live model would solve three-for-three unseen tasks. Live-model evaluations belong to the next slice.
