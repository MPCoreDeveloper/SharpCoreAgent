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
```

## Providers

| Provider key | Models | Credential |
|---|---|---|
| `deepseek` | `deepseek-chat` (tools), `deepseek-reasoner` (no tools) | `DEEPSEEK_API_KEY` |
| `grok` | `grok-4`, `grok-3`, `grok-3-mini` | `XAI_API_KEY` or `GROK_API_KEY` |
| `openai-compatible` | any model the endpoint serves (`--base-url` required) | `OPENAI_API_KEY`, `SHARPCOREAGENT_OPENAI_COMPATIBLE_API_KEY` or `SHARPCOREAGENT_API_KEY` |
| `demo` | `demo-scripted` (deterministic, for tests and demos) | none |

`deepseek` is the default provider. `models` prints the catalogue of the build you have, including
whether each model accepts tool calls.

## Commands and options

```
sharpcoreagent run "<prompt>" [options]      one turn, then exit
sharpcoreagent chat [options]                interactive loop
sharpcoreagent models [--provider <name>]    list the models this build knows
sharpcoreagent --help | --version
```

| Option | Commands | Default |
|---|---|---|
| `--provider <name>` | run, chat | `deepseek` |
| `--model <id>` | run, chat | provider default |
| `--workspace <path>` | run, chat | current directory |
| `--approval <mode>` | run, chat | `ask` |
| `--max-iterations <n>` | run, chat | `24` |
| `--max-output-tokens <n>` | run, chat | `8000` |
| `--max-total-tokens <n>` | run, chat | `250000` |
| `--temperature <x>` | run, chat | `0.2` |
| `--base-url <url>` | run, chat | none — required by `openai-compatible` |
| `--session <path>` | run, chat | `<workspace>/.sharpcoreagent/sessions/<timestamp>.json` |
| `--no-session` | run, chat | off — write no transcript |
| `--json` | run | off — one JSON object per event on stdout |
| `--quiet` / `--verbose` | run, chat | off / off |
| `--` | run | everything after it is part of the prompt |

Environment: `SHARPCOREAGENT_PROVIDER`, `SHARPCOREAGENT_MODEL`, `SHARPCOREAGENT_WORKSPACE`.

In `chat`, the loop understands `/model grok:grok-4` (switch provider and model without losing the
conversation), `/approval`, `/usage`, `/files`, `/session`, `/help` and `/exit` (or `/quit`).

Exit codes: `0` turn completed, `1` turn failed, `2` bad command line, `3` missing credential,
`4` budget exhausted or interrupted, `5` transcript I/O.

## Approval modes

| Mode | Behaviour |
|---|---|
| `readonly` | only read-only tools run; writes, deletes and shell commands are refused, and the refusal is reported back to the model |
| `ask` (default) | read-only tools run; destructive tools prompt for a decision |
| `allow` | everything runs unattended — for trusted workspaces and CI only |

## Tools

`list_directory`, `read_file`, `search_text`, `write_file`, `delete_file`, `run_shell`.

Every path is resolved against the workspace root, and anything that escapes it — `..`, an absolute
path outside, a link pointing out — is refused before the tool runs. Tool output is truncated at a
fixed budget so one large file cannot consume the context window.

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

## Project status

- `specs/001-coding-agent-core` — the agent loop, providers, tools, sessions and CLI are implemented
  and covered by the test suite (183 tests, 0 warnings).
- `specs/002-code-evidence-index` — the index foundations (schema, file scanner, tokenizer, extractor,
  embedder, reader, writer lock) are in place; the retrieval tools and the `index` command are the
  remaining tasks.

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
```

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 MPCoreDeveloper.
