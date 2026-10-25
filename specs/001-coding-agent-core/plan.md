# Implementation Plan: Coding Agent Core

**Branch**: `001-coding-agent-core` | **Date**: 2026-10-23 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-coding-agent-core/spec.md`

## Summary

Build `SharpCoreAgent`: a self-hosted coding agent for .NET 11 / C# 15 that runs a bounded,
streaming tool-use loop against DeepSeek and Grok (xAI) through one provider port, with a
workspace-confined tool belt, three approval modes and a session transcript that can be saved and
re-opened. The deliverable is a solution of five projects plus a test project, and a CLI
(`sharpcoreagent run|chat|models`) that is usable without writing code.

## Technical Context

**Language/Version**: C# 15 (`LangVersion=preview`), `runtime-async=on`, .NET 11 (`net11.0`)

**Primary Dependencies**: BCL (`System.Net.Http`, `System.Text.Json` source generation,
`System.Threading.Channels`), `Microsoft.Extensions.DependencyInjection.Abstractions`,
`Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions`

**Storage**: files — the workspace itself for the tools, `.sharpcoreagent/sessions/*.json` for
transcripts

**Testing**: xUnit v3 on the Microsoft.Testing.Platform; a deterministic `HttpMessageHandler` stub
for provider tests and a scripted model for orchestrator tests; no live endpoint, no API key

**Target Platform**: Windows and Linux (single TFM, no platform-specific build; the shell tool
chooses its launcher at construction time)

**Project Type**: multi-project library + CLI

**Performance Goals**: first text delta forwarded to the console within one frame of arrival;
orchestrator overhead under 5 ms per streamed chunk; a full unit-test run under 60 seconds

**Constraints**: no vendor SDK; no native binaries; streaming must not buffer the whole response;
every tool result bounded; every loop bounded; zero build warnings

**Scale/Scope**: 5 projects, ~40 source files, 6 tools, 2 first-class providers + 1 configurable
OpenAI-compatible provider + 1 offline demo provider, 3 approval modes

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Verdict |
|---|---|---|
| I. The Model Is a Port | Domain references no SDK/HTTP type; providers only in Infrastructure; adding a provider touches no Application file | PASS — `IChatModel`/`IStreamingChatModel` live in Domain, adapters in `Infrastructure/Providers` |
| II. Tools Are the Only Door | No file or process access outside `Infrastructure/Tools`; every call gated | PASS — `Workspace` confinement plus `IToolApprover`; the shell tool is the only `Process` user |
| III. Untrusted Output | Arguments parsed and validated before execution; results bounded | PASS — `ToolArgumentReader` validation, `TextTruncator`, no `eval`/`Invoke-Expression` |
| IV. Every Loop Has a Budget | `AgentOptions` defaults are finite; typed outcome on exhaustion | PASS — `MaxIterations` 24, `MaxOutputTokensPerTurn` 8 000, `MaxTotalTokens` 250 000, `TurnTimeout` 10 min |
| V. Streaming Is the Default | Streaming port is primary; typed event order guaranteed | PASS — deltas forwarded through `IAgentObserver`; ordering asserted in tests |
| VI. Evidence over Assertion | Every behaviour has a test that fails on revert; no network in tests | PASS — stub handler plus scripted model; suite runs offline |
| VII. Zero Warnings, Snapshot Surface | Zero warnings; nullable as errors; public surface snapshotted | PASS with one recorded deviation (see Complexity Tracking) |
| VIII. Secrets Stay in the Process | Credentials from env/config only; redaction; transcripts not committed | PASS — `ICredentialStore`, redacted request descriptions, `.sharpcoreagent/` ignored |

Post-design re-check: unchanged. No gate fails, so Phase 0 may proceed.

## Project Structure

### Documentation (this feature)

```text
specs/001-coding-agent-core/
├── spec.md              # what and why
├── plan.md              # this file
├── research.md          # Phase 0 decisions
├── data-model.md        # Phase 1 types
├── quickstart.md        # Phase 1 validation guide
├── contracts/
│   ├── model-port.md    # provider port, wire mapping, error and retry contract
│   ├── tools.md         # tool contract and the six tool schemas
│   ├── cli.md           # commands, options, exit codes
│   └── session-format.md# on-disk transcript format
└── tasks.md             # Phase 2 task list
```

### Source Code (repository root)

```text
SharpCoreAgent.slnx
Directory.Build.props            # net11.0, C# 15 preview, nullable as errors, analyzers
Directory.Packages.props         # central package versions
global.json                      # pinned SDK
src/
├── SharpCoreAgent.Domain/           # conversation, tools, workspace, events, errors, ports
├── SharpCoreAgent.Application/      # CodingAgent loop, tool registry, options, prompt, session
├── SharpCoreAgent.Infrastructure/   # OpenAI-compatible transport, DeepSeek, Grok, demo, tools, store
├── SharpCoreAgent.DependencyInjection/ # AddSharpCoreAgent(...) composition root
└── SharpCoreAgent.Cli/              # sharpcoreagent: run | chat | models
tests/
└── SharpCoreAgent.Tests/            # unit + contract + integration tests
```

**Structure Decision**: the same layered shape the sibling `SharpCore*` repositories use, because
the constitution makes the layering a gate and because a provider port is only believable when the
vendor code is physically somewhere else. `SharpCoreAgent.Cli` is a thin shell: it parses arguments,
wires the container and renders events; no agent logic lives there, which is what makes the
orchestrator testable without spawning a process.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| Public API snapshot analyzer is enabled for `SharpCoreAgent.Domain` only in this feature (Principle VII names every shipped assembly) | The Application assembly is still finding its shape while the first provider and tool set land; freezing it now produces snapshot churn that hides real API changes in noise | Snapshotting all five assemblies on day one turns every refactor of the loop into a two-file change and makes the first review unreadable; Domain is the surface consumers actually compile against, so it is frozen first |
| A `demo` provider ships in Infrastructure | The quickstart and the CLI must be demonstrable without credentials, and an offline provider is the cheapest way to prove the port seams in an integration test | Shipping only real providers makes the first run "obtain an API key", and it invites live calls into the test suite |


