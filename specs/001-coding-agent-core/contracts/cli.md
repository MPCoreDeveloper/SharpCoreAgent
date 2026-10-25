# Contract: CLI

**Executable**: `sharpcoreagent` (`src/SharpCoreAgent.Cli`). Sits on `SharpCoreAgent.DependencyInjection`; holds
no agent logic beyond parsing, rendering and approval prompting.

## Synopsis

```
sharpcoreagent run "<prompt>" [options]      # one turn, then exit
sharpcoreagent chat [options]                # interactive loop
sharpcoreagent models [--provider <name>]    # list known models
sharpcoreagent --help | --version
```

## Options

| Option | Applies to | Default | Meaning |
|---|---|---|---|
| `--provider <name>` | run, chat | `deepseek` | `deepseek`, `grok`, `openai-compatible`, `demo` |
| `--model <id>` | run, chat | provider default | Model identifier, e.g. `deepseek-chat`, `grok-4` |
| `--workspace <path>` | run, chat | current directory | Root the tools may touch |
| `--approval <mode>` | run, chat | `ask` | `readonly`, `ask`, `allow` |
| `--max-iterations <n>` | run, chat | `24` | Loop bound |
| `--max-output-tokens <n>` | run, chat | `8000` | Per-request output cap |
| `--max-total-tokens <n>` | run, chat | `250000` | Session token budget |
| `--temperature <x>` | run, chat | `0.2` | Sampling temperature |
| `--base-url <url>` | run, chat | — | Required for `openai-compatible` |
| `--session <path>` | run, chat | `.sharpcoreagent/sessions/<timestamp>.json` | Transcript file; `chat` resumes one that exists |
| `--no-session` | run, chat | — | Do not write a transcript |
| `--json` | run | — | Emit a machine-readable event stream on stdout |
| `--quiet` | run, chat | — | Suppress thinking deltas and tool summaries |
| `--verbose` | run, chat | — | Log request descriptions (host and path only) |

Parsing rules: options may appear before or after the prompt; `--` ends option parsing; an unknown
option is a usage error, never ignored; a missing value for an option is a usage error;
`--provider` values are matched case-insensitively. `SHARPCOREAGENT_PROVIDER`, `SHARPCOREAGENT_MODEL` and
`SHARPCOREAGENT_WORKSPACE` are honoured as defaults, with explicit arguments winning.

## Interactive commands (`chat`)

| Input | Behaviour |
|---|---|
| plain text | starts a turn |
| `/model <provider>:<id>` or `/model <id>` | switches the active model for subsequent turns (US2) |
| `/approval <mode>` | changes the approval mode, printing the new effective mode |
| `/usage` | prints input/output tokens per model for the session, `unknown` where unreported |
| `/files` | prints the files and commands from the last turn |
| `/session` | prints the transcript path |
| `/help`, `/exit` | usage, quit |

Ctrl+C during a turn cancels the turn and returns to the prompt; a second Ctrl+C at the prompt exits.

## Output rules

- Assistant text streams to stdout as it arrives; a streamed answer keeps its trailing newline so a
  transcript can be pasted.
- Thinking deltas (DeepSeek `reasoning_content`) are interleaved but visually marked; `--quiet`
  suppresses them.
- A tool call prints `→ tool(args)` before it runs and `← tool: ok|error (N ms)` after.
- A refusal prints `⨯ tool refused: <reason>` on stderr.
- Errors print to stderr as one line prefixed `error:` then a hint line; no stack trace unless
  `--verbose`.
- `--json` emits one JSON object per line: `{"type":"text_delta","text":"…"}`,
  `{"type":"tool_started",…}`, `{"type":"report",…}`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | turn completed and the session saved |
| 1 | turn failed (model error, tool crash, unexpected exception) |
| 2 | usage error (bad option, missing prompt, unknown provider) |
| 3 | missing credentials (the message names the environment variable) |
| 4 | budget exhausted or turn cancelled |
| 5 | session file could not be written or read |

`chat` exits 0 on `/exit` regardless of earlier turn outcomes; the exit code reflects the last turn
only when the process ends because of that turn.

## Help text

`--help` prints the synopsis, the option table above with defaults, the environment variables, the
interactive commands, and one worked example per provider. `models` prints one line per model:
`<provider>:<id>  <display name>  tools=<yes|no>` and, with `--provider`, only that provider's models
plus a note when the credential variable is unset.
