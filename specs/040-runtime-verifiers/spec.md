# Feature Specification: Python and Node verification tools

**Feature**: `040-runtime-verifiers`
**Status**: implemented 2026-11-04

## Problem

`dotnet_verify` is the only built-in verifier. A turn that edits `cart.py` or `discount.js` can therefore finish with no
passing check at all: the verification profiles machinery exists, but no registered tool declares those languages. The
recorded JavaScript and Python evaluation scenarios also had to prove themselves with `run_shell`, which is evidence that
a command ran, not a verifier result the gate can trust.

## What ships

- `python_verify`, running `python -m unittest discover`.
- `node_verify`, running `node --test`.
- Shared `ProcessVerifyTool` that starts every child through `ShellProcessRegistry`.
- `python`/`Python` (`.py`, `.pyw`) and `node`/`JavaScript` (`.js`, `.mjs`, `.cjs`) profiles.
- Default registration beside `dotnet_verify`, so an edit to a Python or JavaScript file cannot complete unverified.
- Prompt rule 5 naming all three verifiers; the .NET nudge and exact unverified sentence are unchanged.
- The JavaScript and Python recorded scenarios now call the new tools.

## User stories

1. **Fix a Python function.** After editing `src/cart.py`, the model calls `python_verify` (optionally with a target) and
   cannot complete until it passes.
2. **Fix a JavaScript function.** After editing `src/discount.js`, `node_verify` runs `node --test` and is the only
   result that closes the JavaScript gap.
3. **Keep the workspace contract.** A target outside the workspace is refused before a process starts; a missing target
   is a readable failure; a missing runtime is an error that names the executable and asks whether it is installed.
4. **Bound the run.** `timeout_seconds` defaults to 300 and is capped at 900; output is bounded and the process is
   killed with the rest of the shell registry's children.
5. **Keep the default gate.** The recorded evaluation remains deterministic and the .NET behavior is byte-compatible.

## Functional requirements

- **FR-001**: `python_verify` and `node_verify` are registered in every default build, sharing the existing
  `ShellProcessRegistry`.
- **FR-002**: `python_verify` runs `python -m unittest discover -v`; a directory target adds `-s <dir>`, and a file
  target adds `-s <parent> -p <file>`.
- **FR-003**: `node_verify` runs `node --test`; an optional target is appended.
- **FR-004**: A target is resolved through `PathSafety`. Outside the workspace is a refusal; a path that does not exist
  is a failure before the executable starts.
- **FR-005**: A child's exit code 0 is a passing result; any other exit code is an error result, but the bounded output
  still travels in the result.
- **FR-006**: Timeout and cancellation kill the process tree. Secret-named environment variables are removed from the
  child by `ChildEnvironment.RemoveSecrets`.
- **FR-007**: `python_verify` declares `.py` and `.pyw`; `node_verify` declares `.js`, `.mjs` and `.cjs`.
- **FR-008**: Both tools are `Destructive`, report a `CommandRun` effect with the exact command, and set `IsTruncated`
  when the output ceiling was reached.
- **FR-009**: `python_verify` uses the standard library (`unittest`), not pytest, requests or any package installation.
  `node_verify` uses Node's built-in runner, not a package script.
- **FR-010**: The Python/JavaScript recorded scenarios use the new tools for their verification step and the baseline is
  promoted only after an independent passing run.

## Non-goals

- Installing Python, Node, pytest, jest or any package.
- Running arbitrary test commands; `run_shell` remains for that, but it cannot close a profile gap.
- TypeScript, JSX, `.pyi`, notebooks, virtual-environment discovery or per-project interpreter selection.
- Changing `run_shell`, `dotnet_verify`, the shell registry or the process-start rule.
- Parallel test processes or a new process door.

## Success criteria

- A Python or JavaScript edit creates a gap that only the matching verifier closes.
- An out-of-root target is refused and no child starts.
- A missing runtime is an error naming the executable.
- Full Release suite is green with zero warnings.
- Recorded evaluation is 3/3 and the updated baseline has no semantic regression.
