# Tasks: Live-model evaluation suite

## Setup

- [x] T001 Add `EvaluationExecution` with recorded/live mode, provider, model and limits.
- [x] T002 Extend `EvaluationArguments` with `--live`, provider/model/endpoint/credential options and bounded limits.

## Execution

- [x] T003 Build the live provider through `AddSharpCoreAgent` and the production `IChatModelResolver`.
- [x] T004 Keep the recorded model replacement and existing recorded limits byte-compatible.
- [x] T005 Add credential preflight, live report naming and recorded-baseline write protection.
- [x] T006 Include `mode`, `provider` and `model` in the report; print live metrics separately.

## Policy

- [x] T007 Reject `--live --check`, provider options without `--live`, and `openai-compatible` without `--base-url`.
- [x] T008 Document that recorded `--check` remains the CI gate and live reports are human-reviewed observations.

## Verification

- [x] T009 Argument tests: mode exclusivity, provider options, limits, base URL and help text.
- [x] T010 Recorded `--check` remains 3/3 at 57,823 input / 260 output tokens.
- [x] T011 Live smoke (`--provider demo`) writes `mode: "live"`, reports live usage and writes no baseline.
- [x] T012 Release build has zero warnings; complete suite green.

## Evidence

- `the recorded gate`: 3/3 passed, 57,823 input / 260 output, 0 unintended changes.
- `the live smoke`: `--live --provider demo --scenario csharp-division` produced a separate report with
  `metrics: live 2,061 in / 16 out`; the demo model correctly failed the scenario because it did not edit the file.
- Focused argument coverage was added to `EvaluationContractTests`.

## Out of scope

Parallel live runs, automatic baseline promotion, tolerance-based scoring, provider benchmarking claims, and live
execution in default CI.
