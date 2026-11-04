# Tasks: Python and Node verification tools

## Tools

- [x] T001 Add `ProcessVerifyTool` with the shared process, timeout, target and output contract.
- [x] T002 Add `PythonVerifyTool` and its `python` profile.
- [x] T003 Add `NodeVerifyTool` and its `node` profile.
- [x] T004 Add `ToolNames.PythonVerify` and `ToolNames.NodeVerify`; update the Domain public API record.
- [x] T005 Register both tools beside `dotnet_verify`, sharing `ShellProcessRegistry`.

## Prompt and evaluation

- [x] T006 Extend operating rule 5 to name all three verifiers; keep the .NET nudge unchanged.
- [x] T007 Change the Python and JavaScript scenarios to use the new verifier tools.
- [x] T008 Allow the new tool names in `EvaluationScenarioLoader` validation.

## Verification

- [x] T009 Profile, command-shape, out-of-root, missing-target and missing-runtime tests.
- [x] T010 Release build zero warnings; complete suite **1,632/1,632**.
- [x] T011 Recorded evaluation 3/3 at 61,573 input / 249 output, 0 unintended changes; promote `eng/evals/baseline.json`.

## Evidence

- New tests: 5 in `RuntimeVerifyToolTests`.
- Scenario tool changes: `python-cart` uses `python_verify` with `tests`; `javascript-discount` uses `node_verify` with
  `test/discount.test.js`.
- Full recorded gate after promotion: 3/3 passed, no semantic regressions.

## Out of scope

Package installation, pytest/jest integration, TypeScript/JSX, interpreter discovery, arbitrary commands as verification,
parallel test runs and any new process-start path.
