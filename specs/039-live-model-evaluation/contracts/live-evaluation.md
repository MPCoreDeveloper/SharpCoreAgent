# Contract: live-model evaluation

## Command surface

| Option | Meaning |
|---|---|
| `--live` | Run through the production provider instead of the recorded script. |
| `--provider <name>` | Provider key; default `deepseek`. Requires `--live`. |
| `--model <id>` | Model id; default the provider's default. Requires `--live`. |
| `--base-url <url>` | Required for `openai-compatible`. |
| `--credential-file <path>` | `NAME=value` file used before the environment. |
| `--max-iterations <n>` | 1–1,000; default 40. |
| `--max-output-tokens <n>` | 256–64,000; default 4,000. |
| `--max-total-tokens <n>` | 1,000–100,000,000; default 250,000. |

`--check` is recorded-only. `--baseline` remains valid only with `--check`. `--scenario` may be used with either mode,
but never with `--check`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every attempted scenario passed and there was no recorded regression. |
| 1 | A task failed, or a recorded baseline regression was found. |
| 2 | Invalid arguments, missing credentials, unknown provider/model, or unreadable credential file. |
| 4 | Cancelled. |

## Report

```json
{
  "schemaVersion": 1,
  "mode": "live",
  "provider": "deepseek",
  "model": "deepseek-flash",
  "createdAtUtc": "2026-11-04T12:00:00+00:00",
  "scenarios": [
    {
      "id": "csharp-division",
      "status": "Passed",
      "outcome": "Completed",
      "iterations": 7,
      "toolCalls": 5,
      "inputTokens": 12345,
      "outputTokens": 678,
      "unexpectedChanges": [],
      "failures": []
    }
  ],
  "summary": {
    "total": 3,
    "passed": 3,
    "failed": 0,
    "skipped": 0,
    "attempted": 3,
    "successRate": 1.0,
    "unintendedChanges": 0,
    "inputTokens": 23456,
    "outputTokens": 1234
  }
}
```

`provider` and `model` are optional for old recorded reports. A missing provider means the recorded fake. Live
reports are not accepted by `ReadReportAsync` as a baseline; the mode check is deliberate.

## Comparison rules

- Recorded: equality on scenario status/outcome, verification, unexpected changes, semantic metrics and the exact
  expected source fragments, as implemented by `BaselineComparer`/`EvaluationGrader`.
- Live: independent grading plus human review. Expected files must exist, but their exact source text is not required;
  a different equivalent implementation is accepted when the independent behavior check passes. Different iteration
  counts, token counts, tool order and final prose are expected and are not regressions.
- No live report is promoted automatically.
