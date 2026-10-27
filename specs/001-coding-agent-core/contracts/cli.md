# Contract: CLI

**Executable**: `sharpcoreagent` (`src/SharpCoreAgent.Cli`). Sits on `SharpCoreAgent.DependencyInjection`; holds
no agent logic beyond parsing, rendering and approval prompting.

## Synopsis

```
sharpcoreagent [options]                     # a session in the current directory (006)
sharpcoreagent run "<prompt>" [options]      # one turn, then exit
sharpcoreagent chat [options]                # interactive loop
sharpcoreagent models [--provider <name>]    # list known models
sharpcoreagent index [options]               # build, inspect or repair the index (002)
sharpcoreagent config [show|get|set|unset|path]   # settings and their provenance (006)
sharpcoreagent --help | --version
```

A bare `sharpcoreagent` starts a session only when both standard streams are terminals; with either
stream redirected it prints the usage block and exits 0, so a script never blocks on a prompt. The
settings file, its precedence and its exit code are in
`specs/006-interactive-start-and-config/contracts/cli-config.md`.

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
| `--credential-file <path>` | all | — | `NAME=value` keys read before the environment; they win over it |
| `--session <path>` | run, chat | `.sharpcoreagent/sessions/<timestamp>.json` | Transcript file; `chat` resumes one that exists |
| `--no-session` | run, chat | — | Do not write a transcript |
| `--json` | run | — | Emit a machine-readable event stream on stdout |
| `--quiet` | run, chat | — | Suppress thinking deltas and tool summaries |
| `--verbose` | run, chat | — | Log request descriptions (host and path only) |

Parsing rules: options may appear before or after the prompt; `--` ends option parsing; an unknown
option is a usage error, never ignored; a missing value for an option is a usage error;
`--provider` values are matched case-insensitively. `SHARPCOREAGENT_PROVIDER`, `SHARPCOREAGENT_MODEL`,
`SHARPCOREAGENT_WORKSPACE` and `SHARPCOREAGENT_CREDENTIAL_FILE` are honoured as defaults, with explicit
arguments winning.

A credential file holds one `NAME=value` per line. Blank lines and lines starting with `#` are
ignored, an optional `export ` prefix is dropped, and a value may be wrapped in matching single or
double quotes; nothing is expanded or escaped. A key in the file wins over the same variable in the
environment, and a blank value counts as unset. The file is read once per command, and a missing or
malformed file exits `3` before the first request with a message naming the file and the line — never
the line's content.

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
| 3 | missing credentials, or a credential file that cannot be read (the message names the variable, and the file and line) |
| 4 | budget exhausted or turn cancelled |
| 5 | session file could not be written or read |
| 6 | the code index could not be created, written or read (002) |
| 7 | the settings file could not be read, written or understood (006) |

`chat` exits 0 on `/exit` regardless of earlier turn outcomes; the exit code reflects the last turn
only when the process ends because of that turn.

## Help text

`--help` prints the synopsis, the option table above with defaults, the environment variables, the
interactive commands, and one worked example per provider. `models` prints one line per model:
`<provider>:<id>  <display name>  tools=<yes|no>` and, with `--provider`, only that provider's models
plus a note when the credential variable is unset.
