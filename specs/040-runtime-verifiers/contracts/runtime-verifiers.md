# Contract: runtime verification tools

## `python_verify`

| Field | Value |
|---|---|
| Safety | `Destructive` |
| Effect | `CommandRun` with the exact command |
| Arguments | `target?` (file or directory, workspace-relative), `timeout_seconds?` (1–900, default 300) |
| Root command | `python -m unittest discover -v` |
| Directory target | `python -m unittest discover -v -s <dir>` |
| File target | `python -m unittest discover -v -s <parent> -p <file>` |
| Profile | `python` / `Python`, extensions `.py`, `.pyw` |

## `node_verify`

| Field | Value |
|---|---|
| Safety | `Destructive` |
| Effect | `CommandRun` with the exact command |
| Arguments | `target?` (file or directory, workspace-relative), `timeout_seconds?` (1–900, default 300) |
| Root command | `node --test` |
| Target command | `node --test <target>` |
| Profile | `node` / `JavaScript`, extensions `.js`, `.mjs`, `.cjs` |

## Result contract

- Exit code 0: `IsError = false`, header says `result passed`.
- Non-zero: `IsError = true`, header says `result failed`, and the bounded runner output is included.
- Refused target: `IsRefused = true`, no process starts.
- Missing target: `IsError = true`, message names the workspace-relative path.
- Missing executable: `IsError = true`, message names the executable and asks whether the runtime is on `PATH`.
- Timeout: `IsError = true`, process tree killed, partial output included when available.
- Every run records `ToolEffectKind.CommandRun`; a passing result with that effect is what closes the profile gap.

## Gate

`VerifyGate` binds registered `IVerificationTool` profiles. A `.py` edit needs a later passing `python_verify`; a
`.js`/`.mjs`/`.cjs` edit needs a later passing `node_verify`. A `run_shell` result never closes either gap. The exact
.NET detail and nudge remain unchanged.
