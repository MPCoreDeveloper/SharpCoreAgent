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
sharpcoreagent mcp serve [options]           # this workspace's index as an MCP server on stdio (013)
sharpcoreagent mcp config --client <name>    # the block that adds it to claude, vscode, visualstudio or codex (013, 022)
sharpcoreagent mcp bench [options]           # the fixture questions, measured against the source (013)
sharpcoreagent ui --serve [options]          # this workspace's index as a page on 127.0.0.1 (023)
sharpcoreagent ide agent [--write]           # the agent profile that puts the index in an editor's picker (024)
sharpcoreagent ide tasks [--write]           # the task that rebuilds the index, for .vscode/tasks.json (024)
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
| `--planner <model>` | run, chat | session model | The model a plan turn runs on; a bare id keeps `--provider`, `provider:id` names both; a settings key and `SHARPCOREAGENT_PLANNER_MODEL` (016) |
| `--executor <model>` | run, chat | session model | The model an act turn runs on, and the one `/model` starts from (016) |
| `--reviewer <model>` | run, chat | session model | The model `/review` sends its read-only turn to (016) |
| `--workspace <path>` | run, chat | current directory | Root the tools may touch |
| `--add-root <path>` | run, chat | — | A read-only root the read tools may also answer from; repeatable, and `SHARPCOREAGENT_ADD_ROOTS` adds more after these (007) |
| `--web` | run, chat | — | Register `fetch_url` for this process; refused on `models`, `index`, `config` and `setup` (008) |
| `--web-allow <host>` | run, chat | — | Exempt a host from `fetch_url`'s address refusal; repeatable, needs `--web`, and has no settings key or environment variable (008) |
| `--mcp <command>` | run, chat | — | Start one stdio MCP server and register the tools it lists for this process; repeatable, refused on `models`, `index`, `config` and `setup`, and has no settings key or environment variable (009) |
| `--client <name>` | mcp config | — | `claude`, `vscode`, `visualstudio` or `codex`: whose configuration the printed block is for; `visualstudio` and `claude` name the same `.mcp.json` under different keys, and `vs` is accepted for `visualstudio`; refused on every other command, and required — `mcp config` without it exits `2` (013, 022) |
| `--write` | mcp config, ide | — | Merge the block into the file `--client` names instead of printing it, keeping every other key, server and entry field; an entry that already matches is not written, and a file that cannot be merged exits `7` untouched; refused on every other command, `--client codex` included (022). On `ide`, write the profile or the task instead of printing it, replacing the profile only when the command wrote it and merging the task by label (024) |
| `--approval <mode>` | run, chat | `ask` | `readonly`, `ask`, `allow` |
| `--max-iterations <n>` | run, chat | `400` | Loop bound: one turn is tens of model calls, not a handful |
| `--max-output-tokens <n>` | run, chat | `8000` | Per-request output cap |
| `--max-total-tokens <n>` | run, chat | `10000000` | Session token budget; `/budget` raises it without a rebuild |
| `--temperature <x>` | run, chat | `0.2` | Sampling temperature |
| `--base-url <url>` | run, chat | — | Required for `openai-compatible` |
| `--credential-file <path>` | all | — | `NAME=value` keys read before the environment; they win over it |
| `--session <path>` | run, chat | `.sharpcoreagent/sessions/<timestamp>.json` | Transcript file; `chat` resumes one that exists |
| `--no-session` | run, chat | — | Do not write a transcript |
| `--json` | run, index, ui | — | Emit a machine-readable event stream on stdout; one object for an index report; the endpoint object for `ui --serve` (023) |
| `--serve` | ui | — | Serve the panel; the only action `ui` has, so `ui` without it exits `2` (023) |
| `--port <n>` | ui | `0` | The port to bind, `0`–`65535`; `0` picks a free one, and a port that cannot be bound exits `2` with the port named (023) |
| `--refresh` | ui | — | Walk the index before each answer, inside `--index-budget`; without it a page load never writes (023) |
| `--quiet` | run, chat | — | Suppress thinking deltas and tool summaries |
| `--verbose` | run, chat | — | Log request descriptions (host and path only) |

Parsing rules: options may appear before or after the prompt; `--` ends option parsing; an unknown
option is a usage error, never ignored; a missing value for an option is a usage error;
`--provider` values are matched case-insensitively. `SHARPCOREAGENT_PROVIDER`, `SHARPCOREAGENT_MODEL`,
`SHARPCOREAGENT_PLANNER_MODEL`, `SHARPCOREAGENT_EXECUTOR_MODEL`, `SHARPCOREAGENT_REVIEWER_MODEL`,
`SHARPCOREAGENT_WORKSPACE` and `SHARPCOREAGENT_CREDENTIAL_FILE` are honoured as defaults, with explicit
arguments winning.

A credential file holds one `NAME=value` per line. Blank lines and lines starting with `#` are
ignored, an optional `export ` prefix is dropped, and a value may be wrapped in matching single or
double quotes; nothing is expanded or escaped. A key in the file wins over the same variable in the
environment, and a blank value counts as unset. The file is read once per command, and a missing or
malformed file exits `3` before the first request with a message naming the file and the line — never
the line's content.

When no model the process may use has a key and both streams are terminals, `run` and `chat` ask for one
instead of refusing: the prompt names the variable, the key is read with the echo off, and it is written to
`credentials.env` beside the settings file (or to the file `--credential-file` /
`SHARPCOREAGENT_CREDENTIAL_FILE` named). The write keeps every other line of that file, replaces an
existing assignment for the same name, and leaves the file readable by its owner only where the platform
has file modes. The key is then set in the process for the rest of the run, and — only when the developer
named no file — the settings document gains `credential-file` pointing at it, so the next run finds it. A
piped stdin, a declined `[Y/n]` or a blank paste writes nothing and exits `3` with the three routes printed
(016).

## The panel (`ui --serve`) (023)

`sharpcoreagent ui --serve` binds `http://127.0.0.1:<port>/` and answers two routes until the process is
interrupted:

| Route | Answer |
|---|---|
| `GET /` | the page: the state, every count `index --status --json` carries, and the workspace path in its footer; `Content-Type: text/html; charset=utf-8` |
| `GET /api/status.json` | the object `index --status --json` prints, byte for byte, from the one builder both surfaces share; `Content-Type: application/json; charset=utf-8` |

Both answers carry `Cache-Control: no-store` and `Connection: close`. Everything else is refused with an
empty body that carries no number: any method but `GET`, and any path other than the two, is `404`; any
request whose `Host` header is not `127.0.0.1:<port>` is `403`, which is what stops a hostile page reaching
the listener by DNS rebinding.

The listener is loopback-only by construction: the prefix is `127.0.0.1`, and no flag, settings key or
environment variable widens it — there is no `--host`. The page carries a workspace path and counts, which
is a map of a private repository, and nothing this command does publishes it to a network.

The default reads the index and never writes it: a page load has `index --status` semantics. `--refresh`
opts into one walk before each answer, bounded by `--index-budget` (default `3000` ms) with the same call
`run` and `chat` make. A read that fails is answered — `503`, with the failure in the body — and the
listener keeps serving.

`--json` prints one object on stdout before the first request is answered —
`{"command":"ui","action":"serve","url":"http://127.0.0.1:<port>/","workspace":"<slash-normalised path>","refresh":<bool>}`
— and then serves, so a script can start the panel and learn where it is without parsing a sentence.
Everything else the command says goes to stderr, the URL first and alone on its line.

It exits `0` when Ctrl+C stops it, the way `mcp serve` ends at end of input, and `2` when the port cannot
be bound — outside `0–65535`, not a number, or already held — with the port named and `--port` as the fix.
It runs no model, keeps no transcript and starts nothing: `--model`, `--provider`, `--planner`,
`--executor`, `--reviewer`, `--session`, `--approval`, `--client`, `--write`, `--mcp` and `--web` are
refused at `2`, and opening the printed URL is the developer's action.

## The editor files (`ide`) (024)

`sharpcoreagent ide <agent|tasks>` answers one question — which of an editor's two files — and prints it,
unless `--write` is given, in which case the file is written and stdout stays empty. Neither action reads
the index, builds a container, reads a credential, starts a process or touches the network.

| Action | File | What it carries |
|---|---|---|
| `agent` | `<workspace>/.github/agents/sharpcoreagent.agent.md` | YAML frontmatter — `description`, and `tools` naming the six published tools as `sharpcoreagent/<tool>` — then the prompt |
| `tasks` | `<workspace>/.vscode/tasks.json` | `"version": "2.0.0"` and one task, `sharpcoreagent: index`, whose `command` is this binary's absolute path |

The profile's tool list is built from the same array `mcp serve` filters by, so the two cannot disagree; it
carries no `name` (the file's name is the name the picker shows), no `model`, no `target` and no
`mcp-servers`. Its last line is a marker naming the command: `--write` replaces the file only when that line
is there, and a profile this command did not write is refused at `7` with the file untouched.

The task is merged by label rather than appended: a task already carrying this label is replaced in place,
every other task and every other top-level key survives, `version` is kept when the file has one and written
when it does not, and a file that already says this is not written at all. A `tasks.json` that is not JSON —
including one with `//` comments, which VS Code accepts and `System.Text.Json` does not — or whose `tasks` is
not an array, exits `7` with the file untouched.

Both actions exit `0` for a document printed, a file created, updated, or already correct, and `7` for a file
that could not be read, merged or written. `ide` with no action, an unknown action, an operand after the
action, and the flags that name a turn or a client (`--provider`, `--model`, `--planner`, `--executor`,
`--reviewer`, `--base-url`, `--fallback`, `--session`), the network (`--web`, `--web-allow`, `--mcp`), an
index action (`--status`, `--rebuild`, `--verify`, `--prune`), `--no-index`, `--add-root`, the panel's flags
(`--serve`, `--port`, `--refresh`), `--no-refresh`, `--client` and `--json` are all refused at `2`.

## Interactive commands (`chat`)

| Input | Behaviour |
|---|---|
| plain text | starts a turn |
| `/model <provider>:<id>` or `/model <id>` | switches the active model for subsequent turns (US2) |
| `/roles` | prints the model each role runs on: plan, act and review (016) |
| `/review [focus]` | one read-only turn on the reviewer's model, in this transcript; the approval mode and the model are restored afterwards (016) |
| `/approval <mode>` | changes the approval mode, printing the new effective mode |
| `/usage` | prints input/output tokens per model for the session, `unknown` where unreported |
| `/files` | prints the files and commands from the last turn |
| `/session` | prints the transcript path |
| `/roots` | prints the primary root and every read-only attachment, with its absolute path (007) |
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
| 0 | turn completed and the session saved; or `mcp serve` reached end of input; or the panel was stopped with Ctrl+C (023) |
| 1 | turn failed (model error, tool crash, unexpected exception) |
| 2 | usage error (bad option, missing prompt, unknown provider); for `ui --serve`, a port outside `0–65535`, a port that is not a number, or a port that cannot be bound (023) |
| 3 | missing credentials, or a credential file that cannot be read (the message names the variable, and the file and line) |
| 4 | budget exhausted or turn cancelled |
| 5 | session file could not be written or read |
| 6 | the code index could not be created, written or read (002) |
| 7 | the settings file could not be read, written or understood (006); or `mcp config --write` could not merge the client's file, which is left as it was (022) |

`chat` exits 0 on `/exit` regardless of earlier turn outcomes; the exit code reflects the last turn
only when the process ends because of that turn.

`mcp bench` exits 0 when every fixture query answered identically in process and over stdio, 1 when one
did not (the query is named on stderr), and 2 when it cannot run: no workspace, no
`specs/013-evidence-mcp-server/bench/queries.txt`, or no built index. The surface it measures is in
`specs/013-evidence-mcp-server/contracts/mcp-server.md`.

## Help text

`--help` prints the synopsis, the option table above with defaults, the environment variables, the
interactive commands, and one worked example per provider. `models` prints one line per model:
`<provider>:<id>  <display name>  tools=<yes|no>  images=<yes|no>` and, with `--provider`, only that
provider's models plus a note when the credential variable is unset. `images=` is the flag `view_image`
consults before it opens a file (017), so the answer to "will this model see my screenshot?" is on the same
line as the answer to "can it call a tool?".
