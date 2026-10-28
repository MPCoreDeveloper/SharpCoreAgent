<!--
Sync Impact Report
==================
Version change: 1.0.2 -> 1.1.0 (MINOR: three constraints materially expanded and one technical
  constraint clarified; no principle removed, no gate relaxed, no existing code path invalidated)
Amended:
  - II. Tools Are the Only Door to the Machine — a workspace MAY carry additional roots the
    developer names explicitly. Those roots are readable and never writable, and a write, a delete
    or a shell command still resolves against the primary root alone. The change is what lets one
    turn read a second project without turning every path on the machine into ambient authority.
  - III. Untrusted Output, Validated at the Boundary — the ban on `Process.Start` outside the shell
    tool gains a second, identically shaped exception: the MCP client. Both run a process the
    developer named on the command line; neither runs one the model chose.
  - VIII. Secrets Stay in the Process — "and nowhere else" is narrowed to what it always meant: no
    telemetry, phone-home or update check. Network access beyond the model endpoint becomes a
    capability the developer opts into per process (`--web`, `--mcp`), reached through a tool that is
    bounded, approval-gated and recorded. Every rule about keys is unchanged.
  - Technical Constraints — a process the developer supplies as an MCP server is not a native binary
    this repository ships or distributes, so it is outside the "no native binaries" rule rather than
    an exception to it.
Added sections: none. Removed sections: none.
Migration: none required. Every default is unchanged — no additional roots, no MCP server, web
  access off — so a build that configures nothing behaves exactly as it did under 1.0.2. The
  surfaces this amendment creates are `--add-root` / `SHARPCOREAGENT_ADD_ROOTS` (read-only attached
  roots), `--web` (fetching a URL) and `--mcp` (a developer-supplied stdio server), each opt-in and
  each documented in `HelpText` and `README.md` in the round that ships it.
Follow-up TODOs: none.
-->

<!--
Sync Impact Report
==================
Version change: 1.0.1 -> 1.0.2 (PATCH: one constraint sentence made explicit and enforceable; no
  principle, section or quality gate added, removed or reworded in intent)
Amended: the Runtime constraint said `net11.0` for every project and excluded `net10.0`. It now says
  the same thing in the direction it was always meant: 11 is a floor, not a ceiling — .NET 10 and
  older, .NET Framework and .NET Standard stay unsupported, and a later `net12.0`/`net13.0` target is
  the same product on a newer runtime rather than an amendment. The floor is now enforced by
  `Directory.Build.targets` (`SCA0001`) instead of relying on every project file to repeat it, and the
  Microsoft.Extensions packages move to the .NET 11 generation (`11.0.0-rc.2.26504.105`) so that no
  .NET 10 library remains in a net11.0 dependency graph. The SharpCoreDB packages are resolved from
  nuget.org; the local-feed requirement, and the build error that explained it, are gone.
Added sections: none. Removed sections: none. Follow-up TODOs: none.
-->
<!--
Sync Impact Report (previous)
==================
Version change: 1.0.0 -> 1.0.1 (PATCH: identity only; no principle, section or gate changed)
Amended: the product, the namespaces, the project names, the assembly name and the configuration
  prefix moved from SharpAgent to SharpCoreAgent. Every principle, constraint and quality gate is
  unchanged in substance; the rename is recorded here because the constitution is the document a
  later reader trusts when the repository name and the code disagree. `SharpAgent.*` -> `SharpCoreAgent.*`,
  `SHARPAGENT_*` -> `SHARPCOREAGENT_*`, `.sharpagent/` -> `.sharpcoreagent/`.
Added sections: none. Removed sections: none. Follow-up TODOs: none.
-->
<!--
Sync Impact Report (previous)
==================
Version change: (none) -> 1.0.0
Added principles: I. The Model Is a Port, Never a Dependency; II. Tools Are the Only Door to the
  Machine; III. Untrusted Output, Validated at the Boundary; IV. Every Loop Has a Budget;
  V. Streaming Is the Default; VI. Evidence over Assertion; VII. Zero Warnings, Snapshot Surface;
  VIII. Secrets Stay in the Process.
Added sections: Technical Constraints; Development Workflow & Quality Gates; Governance.
Removed sections: none.
Follow-up TODOs: none. Every template placeholder was resolved from the product request (a
  self-hosted coding agent in C# 15 / .NET 11 with DeepSeek and Grok as first-class providers) and
  from the conventions already in force in the surrounding SharpCore* repositories.
-->

# SharpCoreAgent Constitution

## Core Principles

### I. The Model Is a Port, Never a Dependency

A language model is something the agent calls, not something the agent is built out of.

- `SharpCoreAgent.Domain` MUST NOT reference any vendor SDK, HTTP type or wire format. It defines the
  conversation, the tool contract, the stream of deltas, usage and failures.
- Every vendor lives in `SharpCoreAgent.Infrastructure` behind `IChatModel` / `IStreamingChatModel`.
  DeepSeek and Grok (xAI) are the two first-class providers and MUST each ship a dedicated,
  tested adapter; any other OpenAI-compatible endpoint is reachable through configuration rather
  than through a new project.
- Adding a provider MUST NOT change a line in `SharpCoreAgent.Application`. If it does, the port was
  drawn around an implementation instead of around the capability.
- Vendor quirks (reasoning traces, tool-call id formats, stream framing, token accounting) are
  translated inside the adapter and MUST NOT leak into the domain model.

**Rationale**: the point of a self-hosted coding agent is that the model is swappable on the day a
better or cheaper one appears. Two providers implemented for real is the cheapest way to prove the
seam is in the right place; one provider proves nothing.

### II. Tools Are the Only Door to the Machine

The agent touches the filesystem, the shell and the network only through a registered tool.

- Every side effect is an `ITool` with a name, a description, a JSON Schema for its arguments and
  a typed result. There is no ambient `File.WriteAllText` in the loop, no "just this once" shell
  call in the orchestrator.
- A model output of `ToolCall` is a *request*, never an authorization. Each call passes the tool
  gate before execution and every execution is recorded in the transcript.
- Path-taking tools resolve their argument against the workspace root and MUST reject any
  resolution that escapes it (including `..`, absolute paths outside the root, and reparse points
  that resolve outside the root). A workspace MAY carry additional roots the developer named
  explicitly, on the command line or in the one environment variable that exists for them; those
  roots are readable and never writable, and a tool that writes, deletes or runs a command resolves
  against the primary root alone.
- Destructive capabilities (write, delete, shell) are gated by an approval mode whose default is
  the safe one when the option is absent rather than when it is present.

**Rationale**: an agent that can act is an agent that can do damage. A single choke point is
greppable, auditable, testable and revocable; a hundred convenient call sites are none of those.

### III. Untrusted Output, Validated at the Boundary

Model output is user input from a stranger. It is parsed, never trusted, never evaluated.

- Tool arguments are parsed from JSON and validated against the tool's schema before the tool's
  code runs. A malformed argument is a tool error reported back to the model, not an exception
  that unwinds the session.
- Tool results returned to the model are bounded in size (a truncated result says that it was
  truncated) so one verbose command cannot silently consume the context window.
- No `eval`, no `Invoke-Expression`, no dynamic assembly loading, no `Process.Start` outside the
  shell tool and the MCP client. Both exceptions have the same shape: a process the developer named
  on the command line, never one the model chose, speaking a documented protocol over a pipe the
  process owns. Shell arguments are passed as an argument list, never concatenated into a command
  string.
- Text produced by the model or by a tool MUST NOT be interpreted as a control instruction by the
  host; only the orchestrator decides what happens next.

**Rationale**: prompt injection is not hypothetical in a coding agent — repository content is
attacker-controlled input the model reads on purpose. The boundary is the process, and it is
enforced in code, not in a system prompt.

### IV. Every Loop Has a Budget

The agent loop is bounded in iterations, tokens and wall-clock time, and the bounds are visible.

- `AgentOptions` carries `MaxIterations`, `MaxOutputTokensPerTurn` and `MaxTotalTokens`; each has a
  default that cannot be interpreted as "unlimited".
- Exhausting a budget ends the turn with a typed outcome that states which budget was exhausted.
  It never throws for the user's benefit and never loops silently.
- Usage reported by the provider is accumulated per turn and per session. When a provider omits
  usage the adapter reports `null` and the session says "unknown" — a fabricated number is worse
  than a missing one.
- Cancellation is honoured at every `await`: a cancelled turn leaves a transcript a caller can
  inspect and resume from.

**Rationale**: an unbounded agent loop is an unbounded bill. A budget enforced only in a UI is not
enforced at all.

### V. Streaming Is the Default

The user watches the agent work, not a spinner.

- `IStreamingChatModel` is the primary port; a non-streaming completion is a convenience that
  aggregates the same deltas.
- The orchestrator emits typed events (`TextDelta`, `ToolStarted`, `ToolCompleted`, `TurnCompleted`,
  `Failed`) through an observer port, so a console, a log and a test consume the same stream.
- Ordering is guaranteed: every `ToolStarted` precedes its `ToolCompleted`, and no delta for a turn
  arrives after `TurnCompleted`. A violation is a bug in the orchestrator, not a rendering quirk.
- Streaming a response MUST NOT require buffering the whole response in memory first.

**Rationale**: latency dominates how an agent feels, and a stream is the only honest way to show
that a long turn is progressing rather than hung.

### VI. Evidence over Assertion

A behaviour is done when a test fails if the behaviour is removed.

- Every behaviour change ships with a test that fails on revert, and the pull request names it.
- Provider adapters are tested against a deterministic `HttpMessageHandler` stub that replays
  recorded request/response pairs, including SSE chunks split mid-event. No test calls a live
  endpoint, and no test requires an API key.
- The orchestrator is tested with a scripted model, so a multi-turn tool exchange is a plain unit
  test that runs in milliseconds.
- Test code contributes no lines to the coverage number it exists to justify.

**Rationale**: a coding agent's failure modes are ordering bugs in a loop and framing bugs in a
stream. Both are invisible to "it worked when I tried it".

### VII. Zero Warnings, Snapshot Surface

- `dotnet build .\SharpCoreAgent.slnx -c Release` MUST finish with zero warnings; nullable warnings are
  errors (`WarningsAsErrors=nullable`) and analyzers run at `latest-recommended` in the build.
- A finding is fixed, not configured away. A rule this repository deliberately does not follow is
  silenced per file with the reason beside it, never repository-wide.
- `PublicAPI.Shipped.txt` is the frozen surface of every shipped assembly; additions land in
  `PublicAPI.Unshipped.txt` in the same commit, and `RS0016`/`RS0017` are build errors.
- The repository follows Semantic Versioning 2.0.0 and the changelog entry exists before the tag.

**Rationale**: an agent SDK is consumed by other code. A tolerated warning is a warning the next
contributor must guess about, and an unsnapshotted public surface is a breaking change waiting for
a victim.

### VIII. Secrets Stay in the Process

- API keys come from environment variables or a git-ignored local configuration file, and never
  reach a log, a transcript, a tool result or an exception message.
- The HTTP layer redacts `Authorization` and any configured header before a request is described.
- Transcripts are written only when asked for, into `.sharpcoreagent/` inside the workspace, which is
  git-ignored.
- No telemetry, no phone-home, no update check. Without an explicit instruction, outbound traffic
  goes to the configured model endpoint and nowhere else. Network access beyond it is a capability
  the developer opts into per process (`--web`, `--mcp`) and never a default, and it is reached
  through a tool that is bounded, approval-gated and recorded like any other.

**Rationale**: the agent runs with the user's credentials on the user's machine. A key in a
transcript is a key in a screenshot, a paste and a support ticket.

## Technical Constraints

- **Runtime**: `net11.0` for every project, `LangVersion` `preview` (C# 15), `runtime-async=on` for
  the libraries. `net11.0` is a floor, not a ceiling: `net10.0` and older — and .NET Framework or
  .NET Standard — are not supported targets for this repository, while a later `net12.0`/`net13.0`
  target is the same product on a newer runtime and needs no amendment. The floor is enforced by
  `Directory.Build.targets` (`SCA0001`), so it fails the build rather than the reader's expectations.
  Targeting a framework at all is a decision, not a build property.
- **Language features in use**: nullable reference types everywhere, file-scoped namespaces,
  primary constructors, collection expressions, `field`-backed properties,
  `System.Threading.Channels` for streaming and `System.Text.Json` source generation for the wire
  format.
- **Dependencies**: the BCL plus `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Http` and `Microsoft.Extensions.Logging.Abstractions`. A new dependency
  needs a reason the BCL cannot satisfy, and the reason goes in the pull request.
- **No native binaries** and no platform-specific code outside the shell tool, which selects its
  launcher (`cmd.exe` on Windows, `/bin/sh` elsewhere) at construction time. A process the developer
  supplies as an MCP server is not a binary this repository ships or distributes: it is named on the
  command line, speaks JSON-RPC over stdio, and is bound by what a shell command is bound by.
- **Determinism**: the same transcript, the same options and the same scripted model produce the
  same events, in the same order, on every run.

## Development Workflow & Quality Gates

- Spec-driven: `/speckit-constitution` → `/speckit-specify` → `/speckit-plan` → `/speckit-tasks` →
  `/speckit-implement` → `/speckit-converge`. Artefacts live in `specs/<###-feature>/` and are
  reviewed like code.
- A task that cannot be verified is not done. Each phase closes with `dotnet build` and
  `dotnet test` on the solution, in Release.
- The layering is enforced by project references and reviewed by eye: Domain → nothing but the BCL;
  Application → Domain; Infrastructure → Domain and Application; DependencyInjection → all;
  Cli → DependencyInjection.
- Complexity is justified in `plan.md` under "Complexity Tracking" before it is written, not
  defended afterwards.

## Governance

The constitution supersedes local preference and precedent. An amendment requires a stated
rationale, the version bump described below, and a migration note for any surface it changes.

- **MAJOR**: a principle is removed or redefined in a way that invalidates existing code, or the
  layering/porting rules change.
- **MINOR**: a principle or section is added, or an existing one is materially expanded.
- **PATCH**: wording, clarification, a typo, or a non-semantic refinement.
- Every pull request states which principles it touches and how compliance was verified. A
  violation that must ship is recorded in `plan.md` under "Complexity Tracking" and approved in
  review; an unrecorded violation is a defect.
- `docs/` and `README.md` are governed as code: a behaviour change that contradicts them is a bug
  in one of the two, and the pull request fixes both.

**Version**: 1.1.0 | **Ratified**: 2026-10-23 | **Last Amended**: 2026-10-28




