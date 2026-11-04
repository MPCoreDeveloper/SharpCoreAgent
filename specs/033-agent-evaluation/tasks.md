# Tasks: Agent task evaluation

## Contract and wiring

- [x] T001 Write spec, plan, scenario/report contract and quickstart; reserve no earlier feature number.
- [x] T002 Add `eng/SharpCoreAgent.Evaluation` and its test reference without changing production tool registration.

## Runner

- [x] T003 Implement strict scenario loading, recorded streaming responses and deterministic usage.
- [x] T004 Implement isolated fixture copying, recursive change auditing and cleanup.
- [x] T005 Run the production agent, verify effects/evidence, execute independent grading and aggregate reports.
- [x] T006 Implement semantic baseline comparison, CLI options, JSON persistence and exit codes.

## Corpus and tests

- [x] T007 Add C#/.NET, Python and JavaScript fixtures with visible and independent checks.
- [x] T008 Add focused tests for loading, success, wrong edits, unexpected files, failed grades, skips and comparisons.
- [x] T009 Run the corpus twice, record the measured baseline, and prove `--check` detects a regression.
- [x] T010 Update README/spec evidence and run Release build plus the complete suite at zero warnings.

## Evidence

- Fifteen new tests: six runner, five contract, two comparison and two recorded-model tests. The complete suite is
  **1,535/1,535** in Release; the solution build reports zero warnings.
- `eng/evals/baseline.json`: C#/.NET, JavaScript and Python all pass; four iterations and three tool calls per task;
  one intended modified source file per task; no unintended changes. Recorded aggregate usage is 52,375 input and 260
  output tokens under the documented stable-message estimate. The increase from 44,209 is the default C# adapter:
  three more tool definitions and the C# prompt rule are part of every recorded request.
- Repeated `--check` runs report no semantic regressions. An artificial baseline with C# iterations changed from four
  to three is rejected with `csharp-division: iterations 3 -> 4` and exit code 1.
- `CheckCannotOverwriteTheBaselineItReads` proves the input file stays byte-identical when input and output paths match.
- The initial grading run exposed flattening of independent grading directories; that was corrected before recording the
  baseline. A second integration issue, the harness's global top-level `Program` colliding with the CLI test host, was
  corrected with a namespaced entry point and is covered by the restored full suite.

## Explicitly deferred

Live providers and costs; a larger external benchmark; parallel runs; changing the agent loop or adding semantic language
adapters; enrolling optional Python/Node runtimes in repository-wide CI. Those are separate slices and are not claimed here.
