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

A live DeepSeek turn, and the same prompt on Grok:

```powershell
$env:DEEPSEEK_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- run "fix the failing test in src" --provider deepseek --model deepseek-chat

$env:XAI_API_KEY = "<your key>"
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider grok --model grok-4

# DeepSeek now, Grok if DeepSeek returns 401 or 5xx: both keys must be set.
dotnet run --project src\SharpCoreAgent.Cli -- chat --provider deepseek --fallback grok:grok-4.7
```

## Providers

| Provider key | Models | Credential |
|---|---|---|
| `deepseek` | `deepseek-chat` (tools), `deepseek-reasoner` (no tools), `deepseek-flash`, `deepseek-v4-pro` | `DEEPSEEK_API_KEY` |
| `grok` | `grok-4`, `grok-3`, `grok-3-mini`, `grok-4.7` | `XAI_API_KEY` or `GROK_API_KEY` |
| `openai-compatible` | any model the endpoint serves (`--base-url` required) | `OPENAI_API_KEY`, `SHARPCOREAGENT_OPENAI_COMPATIBLE_API_KEY` or `SHARPCOREAGENT_API_KEY` |
| `demo` | `demo-scripted` (deterministic, for tests and demos) | none |

`deepseek` is the default provider. `models` prints the catalogue of the build you have, including
whether each model accepts tool calls.

## Commands and options

```
sharpcoreagent run "<prompt>" [options]      one turn, then exit
sharpcoreagent chat [options]                interactive loop
sharpcoreagent models [--provider <name>]    list the models this build knows
sharpcoreagent index [options]               build, inspect or repair the code index
sharpcoreagent --help | --version
```

| Option | Commands | Default |
|---|---|---|
| `--provider <name>` | run, chat | `deepseek` |
| `--model <id>` | run, chat | provider default |
| `--fallback <provider:model>` | run, chat | none — a 401 or 5xx is replayed once there |
| `--workspace <path>` | run, chat, index | current directory |
| `--approval <mode>` | run, chat | `ask` |
| `--max-iterations <n>` | run, chat | `24` |
| `--max-output-tokens <n>` | run, chat | `8000` |
| `--max-total-tokens <n>` | run, chat | `250000` |
| `--temperature <x>` | run, chat | `0.2` |
| `--base-url <url>` | run, chat | none — required by `openai-compatible` |
| `--session <path>` | run, chat | `<workspace>/.sharpcoreagent/sessions/<timestamp>.json` |
| `--no-session` | run, chat | off — write no transcript |
| `--json` | run, index | off — one JSON object per event, or per index report |
| `--color <mode>` | run, chat, models | `auto` — `auto`, `always`, `never`; `auto` honours `NO_COLOR`, `TERM=dumb` and redirection |
| `--mode <mode>` | run, chat | `act` — `plan` advertises and allows only read-only tools |
| `--quiet` / `--verbose` | run, chat | off / off |
| `--` | run | everything after it is part of the prompt |
| `--no-index` | run, chat | off — no index read, write or refresh in this process |
| `--index-budget <ms>` | run, chat | `3000` — the refresh ceiling; `0` keeps reads and stops refreshing |
| `--status` | index | off — coverage and freshness; never writes |
| `--rebuild` | index | off — delete every row and build from scratch |
| `--verify` | index | off — drift against the workspace; never writes, exits `1` on drift |
| `--prune` | index | off — drop rows for files that no longer exist |
| `--embedder <mode>` | index | `hashed-local` — the only mode in this build |
| `--tier <name>` | index | `lean` — the only tier in this build |

Environment: `SHARPCOREAGENT_PROVIDER`, `SHARPCOREAGENT_MODEL`, `SHARPCOREAGENT_WORKSPACE`.

In `chat`, the loop understands `/model grok:grok-4` (switch provider and model without losing the
conversation), `/model ds`, `/model r1`, `/model grok` (aliases), `/approval`, `/mode plan|act`,
`/compact [n]`, `/usage`, `/cost`, `/files`, `/session`, `/help` and `/exit` (or `/quit`).

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

## Tools

`list_directory`, `read_file`, `search_text`, `write_file`, `replace_in_file`, `delete_file`,
`run_shell`, `shell_output`, `shell_stop`.

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
so nothing outlives the session by accident.

Five more answer questions about the code itself from the index instead of from the filesystem:
`search_code` (a question, in words), `find_symbol` (a declaration by name), `find_references`
(callers or callees), `code_context` (a declaration's neighbours) and `index_status` (what the index
holds, and how fresh it is). All five are read-only, none of them opens a database — the index sits
behind `ICodeIndex` — and every block they return carries a `path:start-end` citation and the
`sha256:` of the text it quotes, so an answer can be checked without hunting for it.

Every path is resolved against the workspace root, and anything that escapes it — `..`, an absolute
path outside, a link pointing out — is refused before the tool runs. Tool output is truncated at a
fixed budget so one large file cannot consume the context window.

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
  scan; the suite has since grown to 628 tests — the five retrieval tools, the `index` command, the
  colour palette, fragment edits, plan mode, streaming shell output and multi-block edits, `/cost` with
  its dated price table, `--fallback` and the replay that reports both attempts, and the tests that
  cover them — so the next scan moves both the line count and
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
  of fragments matched through a four-rung ladder. The suite is 628 tests, all green, with no compiler
  warnings.
- `specs/004-spend-and-fallback` — Phase 1 and Phase 2 are complete: `/cost` prices the tokens the
  session reported from a dated table that prints `no price known` (and `total unknown`) rather than
  `$0.00`, the catalogues gained `deepseek-flash`, `deepseek-v4-pro` and `grok-4.7` so there is
  something current to price, and `--fallback provider:model` replays a 401 or 5xx turn once at a model
  the developer named, recording both attempts and billing the answer to the model that gave it.
  Checkpoints, `--resume`, the default-model decisions and MCP are listed in those specs' `tasks.md` as
  not started.

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
