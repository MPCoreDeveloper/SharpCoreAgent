# Contract: `chat --resume`

| Form | Behavior |
|---|---|
| `chat --resume` | List `${workspace}/.sharpcoreagent/sessions/*.json`, newest first. |
| `chat --resume <path>` | Open that transcript. A relative path is rooted at the workspace. |
| `chat --resume` with `--session <path>` | Usage error. |
| `run ... --resume` | Usage error. |
| `--resume` on a command that keeps no transcript | Usage error. |

## Listing output

```text
  2026-11-04 12:00:00Z    7 message(s)  deepseek/deepseek-flash  .sharpcoreagent/sessions/20261104T120000Z.json
1 saved session(s) in .sharpcoreagent/sessions
resume one with: sharpcoreagent chat --resume <path>
```

- Timestamp is UTC with seconds.
- Message count includes every stored message, including tool messages.
- Provider/model is the resumed model authority, not the current command line.
- A file that cannot be read or validated prints `  unreadable  <relative path>` and does not stop the list.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Listed, or opened successfully. |
| 2 | Invalid flag combination. |
| 5 | The named transcript is missing or unreadable. |

## Credential rule

The listing branch runs before `TryEnsureCredentials`. A workspace with no provider key can still list its transcripts.
The named-path existence check also runs before the credential prompt.

## Relationship to `--session`

`--session` remains the explicit path form and keeps its new-or-resume behavior. `--resume` is the discovery and
strict-open form. Neither deletes, rewrites or promotes a transcript.
