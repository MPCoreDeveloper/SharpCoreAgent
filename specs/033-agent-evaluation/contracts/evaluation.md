# Contract: Agent task evaluation

## Invocation

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- [options]
```

- `--repository <path>`: checkout containing `SharpCoreAgent.slnx` and `eng/evals`; otherwise walk upward from the current
  directory and application directory.
- `--scenario <id>`: run one declared scenario; cannot be combined with `--check`.
- `--output <path>`: report destination; default `<repository>/artifacts/evals/latest.json`.
- `--check`: compare against `<repository>/eng/evals/baseline.json` without modifying it. An `--output` path equal to the
  baseline being read is refused before any scenario runs.
- `--baseline <path>`: use another comparison document; meaningful only with `--check`.

To record a deliberately accepted new baseline, name the checked-in path with
`--output eng/evals/baseline.json`, inspect the diff, and commit it separately.

## Scenario document

```json
{
  "schemaVersion": 1,
  "id": "example",
  "language": "text",
  "prompt": "Fix the fixture and verify it.",
  "turns": [
    { "calls": [{ "name": "read_file", "arguments": { "path": "value.txt" } }] },
    { "calls": [{ "name": "replace_in_file", "arguments": { "path": "value.txt", "find": "old", "replace": "new" } }] },
    { "text": "Fixed and verified." }
  ],
  "allowedChanges": ["value.txt"],
  "verification": { "tool": "run_shell", "command": "dotnet --version" },
  "grade": {
    "runtimeProbe": "dotnet --version",
    "command": "dotnet --version",
    "expectedOutput": ["11."],
    "expectedFiles": [{ "path": "value.txt", "contains": ["new"] }]
  }
}
```

`schemaVersion`, `id`, `language`, `prompt`, `turns`, `allowedChanges`, `verification` and `grade` are required. Paths in
`allowedChanges` and `expectedFiles` are relative to the fixture root, non-empty and confined. A response turn with calls
uses `FinishReason.ToolCalls`; one without calls is the final answer. Arguments are JSON objects, serialised verbatim as
the model's `arguments` string. Call ids are `call-<turn>-<position>` using one-based positions.

`verification.command` is optional for `dotnet_verify`, required for `run_shell`, and matched against a `CommandRun`
effect. Any other verifier is rejected by scenario validation. Grading requires `command`; `runtimeProbe`,
`expectedOutput`, `expectedFiles` and `timeoutSeconds` (1–600, default 120) are optional.

## Report document

Camel-case JSON with schema version 1, mode `recorded`, UTC creation time, scenario reports and summary. Per-scenario
fields include status (`passed`, `failed`, `skipped`), model outcome, verification flag, iteration/tool/error counts,
actual/unintended changes, nullable input/output tokens, elapsed milliseconds, commands, final text, tool results and
failures. Tool-result content is bounded to 8,000 characters and carries a truncation flag. No workspace path or runtime
probe output is persisted.

The summary contains total, passed, failed, skipped, attempted, success rate (`passed / attempted`, null when zero),
unintended-change count and recorded input/output totals. Unknown usage remains null rather than becoming zero. The
recorded estimate covers stable messages, tool definitions and recorded responses, not dynamic tool output or provider
billing tokens.

Baseline comparison uses `id`, status, outcome, unverified, iterations, tool calls, errors, refusals, unintended changes
and token counts. It does not compare timestamps, elapsed time, paths, command output or final prose. Decreases are
improvements; increases are regressions. A skipped task is not passed and a previously passing task that skips regresses.
