# Implementation Plan: Python and Node verification tools

**Feature**: `040-runtime-verifiers`
**Status**: implemented 2026-11-04

## Shared process tool

`ProcessVerifyTool` owns the pieces the two tools would otherwise duplicate: schema argument reads, target resolution,
timeout handling, cancellation, bounded output and the result header. It starts children only through
`ShellProcessRegistry.Start`, preserving the repository's one-process-door rule.

The result header is deliberately small and labelled:

```text
kind         unittest
target       tests
command      python -m unittest discover -v -s tests
exit code    0
result       passed
duration     0.4s

<runner output>
```

A failing test run is `IsError = true`; this is the same distinction `dotnet_verify` makes between "the command ran" and
"the verification passed".

## Python

`PythonVerifyTool.DefaultFileName` is `python` on Windows and `python3` elsewhere. A missing target discovers from the
workspace root. A directory becomes `-s <dir>`. A file becomes `-s <its parent> -p <its name>`, avoiding a module-name
guess. The command uses `unittest`, so no third-party package is required.

## Node

`NodeVerifyTool` runs `node --test` and appends a target when present. Node's own runner discovers test files; a
workspace that uses a different runner can still use `run_shell`, but that result does not close the JavaScript gate.

## Gate integration

Both classes implement `IVerificationTool`; no change is needed in `VerifyGate`, `CodingAgent` or `PromptLibrary` beyond
the operating-rule sentence. The existing per-profile ordering means:
- an edit to `a.py` is closed by a passing `python_verify` recorded after it;
- a JavaScript edit is closed by `node_verify`;
- a .NET edit still produces the exact legacy `dotnet_verify` detail and nudge.

## Safety and limits

- Workspace-only targets through `PathSafety`.
- 300-second default, 900-second maximum.
- Child environment secret removal.
- Output bounded by `TextLimits.MaxCommandOutputChars`.
- No process starts for a refused or missing target.

## Evaluation impact

The JavaScript scenario now calls `node_verify` with `test/discount.test.js`; Python calls `python_verify` with `tests`.
Both scenarios' independent grading commands remain `run_shell`, so the check is still independent of the agent's chosen
tool. The recorded baseline moved from 57,823/260 to 61,573/249 because the registry and prompt grew; there are no
semantic regressions and zero unintended changes.
