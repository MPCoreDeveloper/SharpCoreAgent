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

See [`../../specs/033-agent-evaluation/quickstart.md`](../../specs/033-agent-evaluation/quickstart.md) for all options and
the explicit baseline-update procedure, and [`contracts/evaluation.md`](../../specs/033-agent-evaluation/contracts/evaluation.md)
for scenario authoring.
