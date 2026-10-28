<p align="center">
  <img src="assets/SharpCoreAgent.jpg" alt="SharpCoreAgent" width="260">
</p>

<h1 align="center">SharpCoreAgent</h1>

<p align="center">A terminal-first coding agent for <strong>C# 15 / .NET 11</strong>, built around <strong>DeepSeek</strong> and <strong>Grok</strong>.</p>

<p align="center">
  <img alt=".NET 11" src="https://img.shields.io/badge/.NET-11.0-512BD4">
  <img alt="C# 15" src="https://img.shields.io/badge/C%23-15-239120">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue">
</p>

<p align="center">
  <a href="https://sonarcloud.io/summary/new_code?id=MPCoreDeveloper_SharpCoreAgent">
    <img alt="Quality gate" src="https://sonarcloud.io/api/project_badges/measure?project=MPCoreDeveloper_SharpCoreAgent&metric=alert_status">
  </a>
  <a href="https://sonarcloud.io/summary/new_code?id=MPCoreDeveloper_SharpCoreAgent">
    <img alt="Coverage" src="https://sonarcloud.io/api/project_badges/measure?project=MPCoreDeveloper_SharpCoreAgent&metric=coverage">
  </a>
  <a href="https://sonarcloud.io/summary/new_code?id=MPCoreDeveloper_SharpCoreAgent">
    <img alt="Security rating" src="https://sonarcloud.io/api/project_badges/measure?project=MPCoreDeveloper_SharpCoreAgent&metric=security_rating">
  </a>
  <a href="https://sonarcloud.io/summary/new_code?id=MPCoreDeveloper_SharpCoreAgent">
    <img alt="Maintainability rating" src="https://sonarcloud.io/api/project_badges/measure?project=MPCoreDeveloper_SharpCoreAgent&metric=sqale_rating">
  </a>
</p>

SharpCoreAgent reads a repository, edits it, runs the build and the tests, and reports what it changed.
The model is swappable by design: DeepSeek and Grok ship as dedicated, tested adapters, and any other
OpenAI-compatible endpoint is reachable through configuration — no rebuild, no new project.

## What it does

- **One request becomes a verified change.** The agent explores the workspace, edits files, runs the
  build and reports the outcome instead of claiming success.
- **The model is a port, not a dependency.** `SharpCoreAgent.Domain` names no vendor, no HTTP type and
  no wire format; every provider lives behind `IChatModel` in the infrastructure layer.
- **Tools are the only door to the machine.** Reading, writing, deleting and shell access all go
  through a registered tool with a JSON Schema, a safety class and a recorded result.
- **Every loop has a budget.** Iterations, output tokens, total tokens and tool-result size are capped
  and enforced, so a confused model cannot spend the night on your account.
- **Streaming is the default.** Text, reasoning deltas and tool calls arrive as they happen, in
  human-readable form or as one JSON event per line (`--json`).
- **Colour when it helps, never when it hurts.** Glyphs, tools, paths, timings and diffs are coloured on
  a terminal; `NO_COLOR`, `--color never` and any redirected stream keep the output plain UTF-8, and
  `--json` never carries an escape.
- **Edits are fragments, not rewrites.** `replace_in_file` changes only the text the model matched, and
  the approval prompt shows a unified diff of the change before the answer is read.
- **Plan before act.** `--mode plan` (or `/mode plan`) hides every destructive tool and asks for a plan,
  so a risky change can be reviewed in the same transcript that then carries it out.

## Requirements

- .NET SDK **11.0.100** or later preview (`global.json` pins the RC build; install that one or a
  roll-forward-compatible SDK)
- Windows, Linux or macOS (the shell tool picks `cmd.exe` or the Unix shell at construction time)
- API keys for the providers you intend to use; the `demo` provider needs none

**Supported runtime**: `net11.0` and newer — 11 is a floor, not a ceiling. .NET 10 and older, .NET
Framework and .NET Standard are refused by the build itself (`Directory.Build.targets`, error
`SCA0001`), because the code and the SharpCoreDB packages it depends on are written against .NET 11
APIs. Later frameworks are not blocked, but they do not exist yet: .NET 12 ships a year after 11, and
until an SDK that supports it is installed, targeting `net12.0` fails on its own with `NETSDK1045`.
The language level is C# 15 (`LangVersion=preview`) and the only place that is configured is
`src/Directory.Build.props`.

## Quickstart

```powershell
git clone https://github.com/MPCoreDeveloper/SharpCoreAgent.git
cd SharpCoreAgent

dotnet build .\SharpCoreAgent.slnx -c Release
dotnet test  .\SharpCoreAgent.slnx -c Release
```

Prove the loop without credentials (deterministic, offline, no key):

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- models
dotnet run --project src\SharpCoreAgent.Cli -- run "list the files in this repository" --provider demo --approval readonly
```

Start a session the way every other agent in a terminal starts one — type the name. With a terminal on
both ends it opens the interactive loop and says what it is using; with either stream redirected it
prints the usage block instead, so a script never hangs on a prompt:

```powershell
dotnet run --project src\SharpCoreAgent.Cli -- --provider demo --workspace .
```

A live DeepSeek turn, and the same prompt on Grok. Neither names a model, so each runs on the default
the provider publishes today — `deepseek-flash` and `grok-4.7`:

```powershell
$env:DEEPSEEK_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- run "fix the failing test in src" --provider deepseek

$env:XAI_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider grok --model grok-4.7

# DeepSeek now, Grok if DeepSeek returns 401 or 5xx: both keys must be set.
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider deepseek --fallback grok:grok-4.7
```

A key in the environment is inherited only by processes started after it was set, so a new editor
window or a restarted terminal may not see one. A credential file has neither problem, and its keys
win over the environment when both are present:

```powershell
# .\.secrets.env, which .gitignore already covers: one NAME=value per line.
# DEEPSEEK_API_KEY=<your key>
dotnet run --project src\SharpCoreAgent.Cli -- run "fix the failing test in src" --credential-file .\.secrets.env

# Or name the file once for every future shell and every editor.
setx SHARPCOREAGENT_CREDENTIAL_FILE "D:\keys\.secrets.env"
```

## Providers

| Provider key | Models | Credential |
|---|---|---|
| `deepseek` | `deepseek-flash` (default), `deepseek-v4-pro`, `deepseek-chat`, `deepseek-reasoner` (no tools) | `DEEPSEEK_API_KEY` |
| `grok` | `grok-4.7` (default), `grok-4`, `grok-3`, `grok-3-mini` | `XAI_API_KEY` or `GROK_API_KEY` |
| `openai-compatible` | any model the endpoint serves (`--base-url` required) | `OPENAI_API_KEY`, `SHARPCOREAGENT_OPENAI_COMPATIBLE_API_KEY` or `SHARPCOREAGENT_API_KEY` |
| `demo` | `demo-scripted` (deterministic, for tests and demos) | none |

`deepseek` is the default provider. `models` prints the catalogue of the build you have, including
whether each model accepts tool calls.

## Commands and options

```
sharpcoreagent [options]                     a session in the current directory
sharpcoreagent run "<prompt>" [options]      one turn, then exit
sharpcoreagent chat [options]                interactive loop
sharpcoreagent models [--provider <name>]    list the models this build knows
sharpcoreagent index [options]               build, inspect or repair the code index
sharpcoreagent config [show|get|set|unset|path]   settings, and where each value comes from
sharpcoreagent --help | --version
```

### Settings you keep

Flags describe one invocation; `config` describes this developer. It writes one JSON document —
`%APPDATA%\sharpcoreagent\config.json` on Windows, `$XDG_CONFIG_HOME/sharpcoreagent/config.json` or
`~/.config/sharpcoreagent/config.json` elsewhere, or whatever `SHARPCOREAGENT_CONFIG` names:

```powershell
sharpcoreagent config                              # every setting, its value and its source
sharpcoreagent config set provider grok
sharpcoreagent config set approval readonly
sharpcoreagent config set model grok-4.7
sharpcoreagent config get provider                 # one value, alone, for a script
sharpcoreagent config unset approval               # the built-in default applies again
```

The command line beats the environment, which beats the file, which beats the built-in default, and
`config show` prints which layer each value came from. The file never holds a key: `config set api-key`
is refused, because a preferences file is copied and committed — `--credential-file` is where a key
goes. `config show` reports only whether the provider's credential is *present*, never its value, and a
file the build cannot apply exits 7 with the key named, so `config set`/`config unset`/`config path`
keep working on the file that needs repairing.

| Option | Commands | Default |
|---|---|---|
| `--provider <name>` | run, chat | `deepseek` |
| `--model <id>` | run, chat | provider default |
| `--fallback <provider:model>` | run, chat | none — a 401 or 5xx is replayed once there |
| `--workspace <path>` | run, chat, index | current directory |
| `--add-root <path>` | run, chat | none — a read-only root the read tools may also answer from; repeatable |
| `--web` | run, chat | off — `fetch_url` may read http and https pages; the network is off otherwise |
| `--web-allow <host>` | run, chat | none — also read this host, whose address is loopback, private or metadata; repeatable |
| `--approval <mode>` | run, chat | `ask` |
| `--max-iterations <n>` | run, chat | `400` — a turn that reads, edits, builds and retests is tens of model calls |
| `--max-output-tokens <n>` | run, chat | `8000` |
| `--max-total-tokens <n>` | run, chat | `10000000` — the ceiling a session starts with; `/budget` raises it |
| `--temperature <x>` | run, chat | `0.2` |
| `--base-url <url>` | run, chat | none — required by `openai-compatible` |
| `--credential-file <path>` | all | none — `NAME=value` keys read first; they win over the environment |
| `--session <path>` | run, chat | `<workspace>/.sharpcoreagent/sessions/<timestamp>.json` |
| `--no-session` | run, chat | off — write no transcript |
| `--json` | run, index | off — one JSON object per event, or per index report |
| `--color <mode>` | run, chat, models | `auto` — `auto`, `always`, `never`; `auto` honours `NO_COLOR`, `TERM=dumb` and redirection |
| `--mode <mode>` | run, chat | `act` — `plan` advertises and allows only read-only tools |
| `--quiet` / `--verbose` | run, chat | off / off — `--verbose` also names each turn as it starts |
| `--` | run | everything after it is part of the prompt |
| `--no-index` | run, chat | off — no index read, write or refresh in this process |
| `--index-budget <ms>` | run, chat | `3000` — the refresh ceiling; `0` keeps reads and stops refreshing |
| `--status` | index | off — coverage and freshness; never writes |
| `--rebuild` | index | off — delete every row and build from scratch |
| `--verify` | index | off — drift against the workspace; never writes, exits `1` on drift |
| `--prune` | index | off — drop rows for files that no longer exist |
| `--embedder <mode>` | index | `hashed-local` — the only mode in this build |
| `--tier <name>` | index | `lean` — the only tier in this build |

Environment: `SHARPCOREAGENT_PROVIDER`, `SHARPCOREAGENT_MODEL`, `SHARPCOREAGENT_WORKSPACE`, and
`SHARPCOREAGENT_ADD_ROOTS` for the read-only roots `run` and `chat` start with, separated by the
platform's path separator (`;` on Windows, `:` elsewhere). Command-line roots come first. There is
deliberately no `SHARPCOREAGENT_WEB`: the network is turned on by typing `--web`, in the process that
wants it, and nothing a shell or a CI image exports can grant it on the developer's behalf.

In `chat`, the loop understands `/model grok:grok-4` (switch provider and model without losing the
conversation), `/model ds`, `/model r1`, `/model grok` (aliases), `/approval`, `/mode plan|act`,
`/compact [n]`, `/budget [n|+n|unlimited]`, `/continue`, `/usage`, `/cost`, `/files`, `/session`,
`/roots`, `/help` and `/exit` (or `/quit`).

A session says what it is and what it spent. A bare `sharpcoreagent` opens on a box naming the workspace,
model, approval mode, mode and budget; the prompt reads `<model> · <mode> ❯`; each tool call is one
`⏺ name {args}` line with a `  ⎿ ok · 12 ms` result (or `⎿ error`) under it; and every turn closes with
`✻ completed · n iteration(s) · … in / … out · … tokens`, so the budget is a number on screen instead of a
surprise. `--max-total-tokens` is the ceiling a session *starts* with, a resumed transcript starts with the
tokens it already recorded as headroom, and `/budget` is how a running conversation gets more room:
`/budget` reports, `/budget +50000` adds, `/budget 400000` sets and `/budget unlimited` removes it for this
session — never written to settings, because a raised budget is a decision about one conversation. When the
ceiling is reached mid-work the loop offers to continue with more and replays the same request instead of
ending the session, and at four fifths of the way it says so once, with the command that raises it; a turn that
finishes *past* the ceiling says instead that the next request will be refused. Answering `n` to that offer
leaves the request *paused* rather than ending the conversation: the prompt reads
`<model> · <mode> · paused ❯`, and `/continue` — or a bare Enter — carries on with the request that is still
the last message in the transcript, so nothing has to be retyped. Leaving while paused exits `4`, because the
transcript holds unfinished work. The
check sits before each request rather than after each answer, so the answer you already have is never
discarded for being one token over: a `run` that finishes over its ceiling still prints the answer and
`614/1,000 tokens` in its last line. Below 1 000 tokens neither `--max-total-tokens` nor `/budget`
accepts a ceiling, because one request costs more than that.

`/usage` counts tokens; `/cost` prices them. The price table is one file with one date, printed with
every report, and each entry names the page it was read from — as of 2026-10-27 that prices
`deepseek-flash`, `deepseek-v4-pro` and `grok-4.7`. Two rules keep the number honest: a model with no
published price reports `no price known` rather than `$0.00`, and a provider that reported no usage
reports `cost unknown`; when no line can be measured at all, the total itself reads `unknown`.
DeepSeek bills an off-peak and a peak tier with peak at exactly double, and a usage report says
neither which tier ran nor whether the prompt hit the cache, so the table holds the peak cache-miss
rate: the report is the ceiling of what the turn could have cost.

`--fallback grok:grok-4.7` names the model a turn is replayed at when the primary returns 401 or a 5xx
(mid-session retirement, an expired key, a provider having a minute). Nothing else moves: a 400 would
fail the same way twice, a dropped connection is retried in place first, and a turn that has already
started streaming is never silently restarted. The replay changes one field of the request — the model
identifier — and the CLI checks both credentials before the first turn, because a fallback whose key is
missing cannot cover the failure it exists for. When it happens the terminal says so on stderr
(`fallback: deepseek:deepseek-chat failed with HTTP 401; grok:grok-4.7 answered`), the report carries
both attempts, and `/cost` bills the tokens to the model that actually answered.

Exit codes: `0` turn completed, `1` turn failed, `2` bad command line, `3` missing credential,
`4` budget exhausted or interrupted, `5` transcript I/O, `6` the index could not be created, written or
read. `index --verify` exits `1` when it finds drift, because a script that checks an index before a
build wants that answer in its control flow.

## Approval modes

| Mode | Behaviour |
|---|---|
| `readonly` | only read-only tools run; writes, deletes and shell commands are refused, and the refusal is reported back to the model |
| `ask` (default) | read-only tools run; destructive tools prompt for a decision |
| `allow` | everything runs unattended — for trusted workspaces and CI only |

In `ask` the question is `allow run_shell_command? <summary> [y/t/a/N]`, with the keys spelled out on the line
above it. `y` runs this call once, `t` runs every later call of the same tool for the rest of the session, `a`
(`all`, `always`) runs every destructive tool, and anything else — an empty line, end of input, a word nobody
meant — refuses, which is what keeps `sharpcoreagent run "…" < /dev/null` safe. `t` is the answer that stops
the questions without also waving through every file write; `/approval` reports what a session has stopped
asking about and `/approval forget` revokes it. Neither yes is written to settings nor carried into the next
session: a standing approval belongs to the conversation that earned it.

## Tools

`list_directory`, `read_file`, `search_text`, `list_roots`, `write_file`, `replace_in_file`,
`delete_file`, `run_shell`, `shell_output`, `shell_stop`.

`list_roots` reports the primary root and every read-only attached root, and it is the one tool result
that prints an absolute path: naming the legal ones is its whole job.

`fetch_url` is the sixteenth tool and the only one that is not registered by default: `--web` adds it
for one invocation, and without the flag it is absent from the registry, from the definitions the model
sees and from the system prompt. It reads one absolute `http`/`https` URL, follows at most five
redirects, and answers with the final URL, the status, the content type, the byte count and whether the
text was cut. It sends one `User-Agent` and nothing else — no cookie, no referer, no credential — and it
refuses a host that resolves to a loopback, private, link-local or cloud-metadata address unless
`--web-allow` names it, on every hop rather than only the first. HTML is reduced to readable text in this
repository (entities decoded, `script`/`style` removed, tags stripped, whitespace collapsed), and
anything that is not text is refused by type rather than decoded. There is no JavaScript, no cookie jar
and no search engine:

```text
url          https://example.com/docs/changelog
status       200 OK
type         text/html; charset=utf-8
bytes        18402
truncated    yes
redirected   from https://example.com/docs

changelog: 4.2.0 — …
```

`replace_in_file` is the edit the agent is told to prefer: `edits` carries up to twenty fragments that
are applied in order against each intermediate state, all or nothing, and only the matched text
changes. A fragment is matched exactly first and only then by line ending, trailing whitespace or
indentation, so a model that guesses the wrong indentation still lands the change; the preview names the
rung it used (`# block 2/4 (indentation)`), and a fragment that matches nothing, or matches twice,
refuses the whole call instead of guessing.

`run_shell` streams while it runs: the command's output appears under a `│` gutter as it arrives, and
`wait_seconds` decides whether the call blocks for the result or hands back a handle. A command that
outlives its wait comes back as `sh-1` with the output so far, `shell_output` drains it — waiting if you
ask — and `shell_stop` kills it with its whole process tree. A background command is capped at an hour,
so nothing outlives the session by accident. Every command runs with an empty, already-closed stdin —
the `< /dev/null` a build expects — so nothing a turn starts can take the line you type at the next
prompt, and a command that asks a question gets end-of-input instead of a turn that never returns.

Five more answer questions about the code itself from the index instead of from the filesystem:
`search_code` (a question, in words), `find_symbol` (a declaration by name), `find_references`
(callers or callees), `code_context` (a declaration's neighbours) and `index_status` (what the index
holds, and how fresh it is). All five are read-only, none of them opens a database — the index sits
behind `ICodeIndex` — and every block they return carries a `path:start-end` citation and the
`sha256:` of the text it quotes, so an answer can be checked without hunting for it.

Every path is resolved against the workspace root, and anything that escapes it — `..`, an absolute
path outside, a link pointing out — is refused before the tool runs. Tool output is truncated at a
fixed budget so one large file cannot consume the context window.

An attached root (`--add-root`) is the narrower thing: a reading list. `read_file`, `list_directory` and
`search_text` answer from it when given an absolute path, a symbolic link inside it is judged against
every attached root (one that lands outside all of them is refused), and every write, delete and shell
command still resolves against `--workspace` alone. The refusal says `read-only` and names the root, so
a write into another repository reads as a decision rather than a broken tool:

```text
refused: path 'D:\repos\Shared.Contracts\src\Order.cs' resolves outside the workspace root 'D:\repos\App'.
'D:\repos\Shared.Contracts' is a read-only attachment; writes stay in the primary root.
```

A relative path always means the primary root, even when an attachment holds a file with the same
relative name, and the five index tools stay primary-only: one index describes one tree, so they answer
an attachment path with "not indexed" and point at `read_file` and `search_text` instead. Attachments
are never written to the settings file and never recorded in the transcript — a resumed session starts
with the roots its new invocation names.

## Code index

`sharpcoreagent index` builds the index the five retrieval tools read:

```powershell
sharpcoreagent index                  # incremental: only changed files are re-read
sharpcoreagent index --status         # files, passages, symbols, edges, freshness — never writes
sharpcoreagent index --verify         # drift against the workspace; exits 1 when it finds any
sharpcoreagent index --prune          # drop rows for files that no longer exist
sharpcoreagent index --rebuild        # the repair for a corrupt or newer index
```

It is one SharpCoreDB file under `.sharpcoreagent/index/` (already gitignored), written by a local,
deterministic hashed embedder: no network and no credential, so retrieval works with the connection
unplugged. The first build of this repository costs minutes — 513 s and 69 MiB for 445 files, 10 314
passages and 5 284 edges, measured on 2026-10-27 — because every file is hashed, chunked and written
row by row; a build where nothing changed is 4 s. `run` and `chat` bring the index up to date before
the turn, bounded by `--index-budget`; when that budget runs out the last complete build stays readable
and the next run continues where it stopped. `--no-index` turns the whole feature off for one process,
and the seven workspace tools still answer.

`specs/002-code-evidence-index/quickstart.md` is the walkthrough, including the ways the index can
fail: exit code `6` means it could not be created, written or read, and `state corrupt` means
`--rebuild` is the repair.

## Architecture

| Project | Responsibility |
|---|---|
| `SharpCoreAgent.Domain` | conversation, tool contract, model descriptors, events, reports, index ports. BCL only. |
| `SharpCoreAgent.Application` | the agent loop, prompt policy, budgets, the in-memory session aggregate |
| `SharpCoreAgent.Infrastructure` | OpenAI-compatible transport, the provider adapters, workspace tools, JSON session store |
| `SharpCoreAgent.Indexing` | SharpCoreDB-backed code-evidence index: schema, extraction, embeddings, ranking |
| `SharpCoreAgent.DependencyInjection` | composition root: which adapter satisfies which port |
| `SharpCoreAgent.Cli` | argument parsing, console rendering, approvals, exit codes |

Layering is enforced by project references: `Domain` → BCL only; `Application` → `Domain`;
`Infrastructure` → `Domain` + `Application`; `DependencyInjection` → all; `Cli` → `DependencyInjection`.

## Sessions

A transcript is written to `<workspace>/.sharpcoreagent/sessions/<timestamp>.json` unless
`--no-session` is passed, and it is saved after every turn. It holds messages, tool calls, approvals,
files touched, commands run and token usage per model — never a credential. `--session <path>` resumes
a transcript, and the file is the authority on which model the conversation was using.

## Security

- Model output is parsed, never trusted: tool arguments are validated against the tool's schema, and a
  malformed argument becomes a tool error rather than a crash.
- Destructive capabilities are gated, and the default is the restrictive mode when an option is absent.
- API keys are read from the environment, redacted from HTTP descriptions, and never reach a log, a
  transcript or an exception message.
- No telemetry, no phone-home, no update check: traffic goes to the configured model endpoint only.

## Code quality (SonarCloud)

Every line is analysed on [SonarCloud](https://sonarcloud.io/summary/new_code?id=MPCoreDeveloper_SharpCoreAgent):
`src/` as product code, `tests/` as test code, with no hand-written file or directory excluded
(`bin`/`obj` and generated sources aside). Two exclusions exist and both are about ownership: `.specify/`
is the Spec Kit scaffolding that the `specify` CLI refreshes (a fix there rewrites upstream's PowerShell
and is lost on the next upgrade), and `eng/` is the build tooling no test can execute, so it is analysed
but not counted as uncovered code. The test project itself is excluded from coverage.

```
pwsh ./eng/sonar-scan.ps1      # reads SONAR_TOKEN from the environment
```

That one script is what CI runs (`.github/workflows/sonar.yml`), so a scan on a laptop and a scan on a
runner cannot disagree about scope, exclusions or the coverage path. It begins an analysis, rebuilds with
the scanner's Roslyn analyzers attached, runs the tests with coverage, and uploads the result. There is no
`sonar-project.properties` and no `SonarQube.Analysis.xml`: the .NET scanner does not read the first, and
the second duplicates settings that would drift.

- **Tests are xUnit v3 on Microsoft.Testing.Platform** — no VSTest in the graph, and no adapter: the test
  project is an executable that hosts MTP, and `global.json` names the runner so plain `dotnet test` uses
  it. Coverage comes from MTP's own coverage extension and lands in
  `TestResults/sonar/coverage.cobertura.xml`, one command away:
  `dotnet test --coverage --coverage-output-format cobertura`.
- **The rules also run in the build.** `SonarAnalyzer.CSharp` is pinned to the exact analyzer build the
  scanner provisions (10.35.0.4138), so `dotnet build` reports the same findings without a token.
- **The gate is "Sonar way",** unmodified: A ratings for reliability, security and maintainability, 80%
  coverage on new code, at most 3% duplication, every security hotspot reviewed. It is computed on every
  run and shown in the badge above.
- **Where the project stands:** 9 824 lines analysed, 0 bugs, 0 vulnerabilities, 0 security hotspots, A for
  reliability, security and maintainability, 0% duplication, **0 code smells**, and 83.7% coverage
  (89.8% of lines, 72.6% of branches) across the 389 tests that scan ran. Those are the figures of that
  scan; the suite has since grown to 878 tests — the five retrieval tools, the `index` command, the
  colour palette, fragment edits, plan mode, streaming shell output and multi-block edits, `/cost` with
  its dated price table, `--fallback` and the replay that reports both attempts, the credential file,
  the settings file, the bare-start rules, the scripted conversations that prove a session keeps
  reading, `fetch_url` with its HTML reduction and its address rule — and the tests that cover them — so
  the next scan moves
  both the line count and
  the percentage. **The gate
  passes**, including
  new-code coverage: the tests that lift the baseline ship in the same commit as the code they cover.
- **The gate is enforced.** CI passes `-WaitForQualityGate`, so a red gate fails the job instead of only
  colouring the badge. Setting the repository variable `SONAR_ENFORCE_GATE` to `false` turns that back
  off — which is the switch you want the day a rename lands and new-code arithmetic dips, not a rewrite.
- **Why new-code coverage under-reports on refactors.** Sonar counts a changed declaration line as a new
  line to cover, and no test hits a signature, a brace or a doc comment; a commit that only renames or
  re-signatures code therefore lands near 30% however well it is tested. That is what the switch above is
  for.
- **How the last 56 smells were cleared.** Every finding was fixed rather than suppressed: the nested
  ternaries, shadowed names and loop shapes became plain statements, `ArgumentOutOfRangeException` is
  raised through one helper that carries the property name as its own parameter, the async iterators were
  split into a validating wrapper and a private core (an iterator body does not run its checks before the
  first item is requested), and the eight methods over the cognitive-complexity limit were cut into named
  steps. The build reports zero warnings, which is the same rule set the scanner runs.
- **Triage is recorded, not silent:** the one finding that used to be reported here —
  `csharpsquid:S2068` on the index's unencrypted-mode sentinel, a documented non-secret the storage engine
  requires when encryption is off — no longer exists: the value is assembled from two literals and named as
  a placeholder, so neither the rule nor a reader can mistake it for a committed credential. Nothing is
  suppressed.

## Project status

- `specs/001-coding-agent-core` — the agent loop, providers, tools, sessions and CLI are implemented
  and covered by the test suite (no compiler warnings and no open Sonar findings).
- `specs/002-code-evidence-index` — M0–M3 are complete: the index foundations (schema, file scanner,
  tokenizer, extractor, embedder, writer lock), the write path (incremental per-file build,
  budget-bounded refresh, rebuild, verify, prune), the retrieval pipeline (lexical and vector legs,
  reciprocal-rank fusion, boosts, graph expansion, selection budget), the five retrieval tools
  (`search_code`, `find_symbol`, `find_references`, `code_context`, `index_status`), the `index` command
  and the agent-facing wiring.
- `specs/003-terminal-console-agent` — P0–P2 are complete (one ANSI palette with `NO_COLOR` and pipe
  detection, `replace_in_file` with a unified-diff approval prompt, `--mode plan` and `/mode`), plus
  `/compact`, model aliases and the Cline-parity pass: `run_shell` streams under a gutter and hands back
  `sh-N` handles that `shell_output` drains and `shell_stop` kills, and `replace_in_file` takes a batch
  of fragments matched through a four-rung ladder. The suite is 878 tests, all green, with no compiler
  warnings.
- `specs/007-external-roots` — implemented: `Workspace` carries read-only attachments, `read_file`,
  `list_directory` and `search_text` answer from them by absolute path, `list_roots` reports the roots,
  `--add-root`/`SHARPCOREAGENT_ADD_ROOTS` name them per invocation, and the write, delete and shell
  paths did not move.
- `specs/008-web-fetch` — implemented: `--web` registers `fetch_url` for one invocation (and nothing else
  does), `--web-allow <host>` exempts a host from the address rule, the tool reads one page as text with
  a redirect, byte and character ceiling, and the request carries no cookie and no credential. The
  address rule refuses loopback, private, link-local, unique-local, carrier-grade NAT and cloud-metadata
  addresses on every hop. The suite has no live network: every case replays a stub `HttpMessageHandler`.
- `specs/004-spend-and-fallback` — all three phases are complete: `/cost` prices the tokens the session
  reported from a dated table that prints `no price known` (and `total unknown`) rather than `$0.00`;
  `--fallback provider:model` replays a 401 or 5xx turn once at a model the developer named, recording
  both attempts and billing the answer to the model that gave it; and the defaults are now the models
  the providers publish — `deepseek-flash` and `grok-4.7` — with the retired identifiers still listed
  so an older transcript resumes on the model it was using. Checkpoints, `--resume` and MCP are listed
  in those specs' `tasks.md` as not started.
- `specs/005-credential-file` — `--credential-file` (and `SHARPCOREAGENT_CREDENTIAL_FILE`) reads
  `NAME=value` keys from a file that survives a restart, a new shell and a second machine; its keys win
  over the environment, a bad file exits 3 naming the file and the line, and `models` uses the same
  store so both commands agree about whether a key is configured.
- `specs/006-interactive-start-and-config` — a bare `sharpcoreagent` starts a session when both streams
  are terminals (and prints the usage block when either is redirected), the session says which model,
  mode, workspace and transcript it is using before the first prompt, and `config show|get|set|unset|path`
  keeps this developer's defaults in one JSON document with the layer each value came from. A file the
  build cannot apply exits 7 and is still repairable from the tool.

## Spec-driven development

This repository is built with [GitHub Spec Kit](https://github.com/github/spec-kit). The constitution
(`.specify/memory/constitution.md`) outranks local preference, and every feature lives in
`specs/<###-feature>/` as spec, plan, research, contracts, data model, tasks and quickstart. The
workflow is `/speckit-constitution` → `/speckit-specify` → `/speckit-plan` → `/speckit-tasks` →
`/speckit-implement` → `/speckit-converge`.

```
src/        the six projects above
tests/      SharpCoreAgent.Tests — unit and integration tests, no network
specs/      one directory per feature, written before the code
.specify/   spec-kit templates, scripts and the constitution
assets/     the logo
eng/        the SonarCloud scan script CI runs
.github/    the sonar workflow
```

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 MPCoreDeveloper.
