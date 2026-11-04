# Contract: `credentials --check`

## Command

```text
sharpcoreagent credentials --check [--provider <name>] [--credential-file <path>]
```

`--check` is required by the documented command and rejected on every other command. `--provider` narrows the
providers; `--credential-file` uses the same file parser and precedence as a turn.

## Output

One header row, then one row per accepted credential name:

| Column | Meaning |
|---|---|
| provider | `deepseek`, `grok`, `openai-compatible` or `demo` |
| credential | the environment/file name, e.g. `XAI_API_KEY` |
| source | `file:<absolute path>:<line>`, `environment`, or `missing` |

A provider with no credential names prints `(no credential required)`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every listed provider has at least one accepted key configured. |
| 1 | At least one listed provider has none. |
| 2 | Invalid arguments or a misplaced `--check`. |
| 3 | The credential file is missing, unreadable or malformed. |

## Safety

The command never prints a value, length, prefix or hash. It reads only the named credential file and environment, does
not open a workspace, resolve a model, start a process or write a file.
