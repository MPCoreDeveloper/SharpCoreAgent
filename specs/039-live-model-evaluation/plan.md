# Implementation Plan: Live-model evaluation suite

**Feature**: `039-live-model-evaluation`
**Status**: implemented 2026-11-04

## Composition

`EvaluationExecution` is the run's mode and limits. `EvaluationArguments` parses the closed CLI surface and rejects
cross-mode combinations before any workspace or provider is created. `EvaluationCli` chooses the report path, checks
live credentials, loads the same corpus and delegates to `EvaluationRunner`.

`EvaluationRunner` keeps the recorded path exactly as it was: a `RecordedChatModel` replaces `IChatModelResolver` and
uses the synthetic recorded descriptor. In live mode it registers no replacement and resolves the selected production
model. Both modes replace only `IToolApprover` with the fixture-scoped approver; no product code gains an "allow all"
setting.

## Data flow

```text
CLI arguments
  -> EvaluationExecution { mode, provider, model, limits }
  -> EvaluationRunner
       -> EvaluationWorkspace (temporary copy of workspace/)
       -> AddSharpCoreAgent(provider, model, workspace, caps)
       -> CodingAgent.RunTurnAsync(prompt)
       -> file-change audit + verification evidence + independent grade
  -> EvaluationReport { mode, provider, model, scenarios, summary }
```

## Cost and rate-limit controls

- `--max-iterations` bounds how many model requests a scenario can make.
- `--max-output-tokens` bounds each response.
- `--max-total-tokens` bounds the whole scenario and is enforced by the existing session budget code.
- Scenarios run sequentially; this is deliberate because it keeps one shared provider key and one rate-limit budget
  easy to reason about.
- No automatic retry policy is added by the evaluator. The production resolver's normal retry/fallback behavior still
  applies; a maintainer should not add `--fallback` to a live evaluation unless the comparison policy explicitly allows
  a different answering model.

## Credentials

`EnvironmentCredentialStore.FromFile` reads the named credential file, then the environment. The evaluator checks
`ModelCatalog.CredentialNames(provider)` and fails with exit code 2 before a workspace is copied. The store's values are
never written to stdout, stderr or the report.

## Nondeterminism

The recorded baseline comparer rejects non-recorded reports. Live mode sets `mode: "live"` and is therefore ineligible
for `--check` both by argument validation and by the baseline reader. Live reports are dated observations; the three
task grades and the `unexpectedChanges` list are the reviewable result. Expected files must exist in both modes, but a
live run does not require the exact source fragment recorded in the scenario: the independent grading command decides
whether an alternative implementation is correct.

## Safety

- The live run uses a fresh temporary workspace per scenario, never the caller's working tree.
- The approval mode is allow-all only for that fixture, exactly as in recorded mode.
- `--output` is checked against the recorded baseline path so a typo cannot overwrite the gate.
- A live run cannot be started accidentally by an omitted flag.
