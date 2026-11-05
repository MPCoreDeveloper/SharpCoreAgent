# Tasks — 009 MCP client

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the
next pass has a real list rather than a memory.

## Phase 1 — the protocol, before any process

- [X] T001 `McpClientOptions` with the two request budgets (20 s control, 120 s call), the page ceiling
  (10), the character ceiling (64 000) and the protocol version the client sends (`2025-06-18`)
  (FR-003, FR-004, FR-009, FR-011).
- [X] T002 `JsonRpcPipe` over an arbitrary input/output `Stream`: one reader loop, one write gate, ids
  from one counter, `RequestAsync`/`NotifyAsync`, `-32601` for a server request the client does not
  implement, and every pending waiter failed when the pipe closes (FR-003, FR-013).
- [X] T003 Timeouts and cancellation inside `RequestAsync`: one linked source per request, a timeout
  message naming the method and the seconds, and the caller's cancellation rethrown as
  `OperationCanceledException` rather than reported as a failure (FR-011).
- [X] T004 `McpToolName.Fold`/`ForTool`/`ForServer`: `[a-z0-9_]`, runs collapsed, ends trimmed, the
  64-character cap, and the `_2` suffix for a repeated server stem (FR-005).

## Phase 2 — the process

- [X] T005 `McpLauncher.For(commandLine)`: `cmd.exe /d /s /c` on Windows, `/bin/sh -c` elsewhere, one
  argument, no tokenising (FR-001, spec `Decisions` 2). As built: the Windows line is a raw argument
  string (`/d /s /c "<the command line>"`) rather than one argv element — an argv element escapes the
  developer's own quotes into `\"…\"`, which `cmd` then tries to execute as a path; the process test
  found it. Unix keeps the argv form, which is what `sh -c` wants.
- [X] T006 `McpServerConnection.StartAsync`: start the child with three redirected streams, build the
  pipe, `initialize`, read the answer, `notifications/initialized`, then `tools/list` with cursor
  pagination (FR-003, FR-004, FR-012).
- [X] T007 Startup failures: a process that will not start, a handshake that times out, malformed JSON,
  a missing `result` and an empty tool list all become one `McpStartupException` naming the command and
  the reason (FR-012).
- [X] T008 `CallAsync`: `tools/call` with the argument object forwarded unchanged, `isError` honoured,
  a JSON-RPC error object flattened into a failure naming the code, and a dead server reported as a
  failure naming the exit code (FR-008, FR-010, FR-013).
- [X] T009 stderr: one drain loop into a 200-line ring, echoed through the host's log line by line when
  one is set, never parsed and never merged with stdout (FR-014).
- [X] T010 Disposal: `Kill(entireProcessTree: true)`, the pipe closed, and a `ProcessExit` handler as
  the backstop (FR-015).

## Phase 3 — the tools

- [X] T011 `McpToolInfo` parsed from `tools/list`: name, description, `inputSchema`, `readOnlyHint`;
  `ReadOnly` only for an explicit `true`, `Destructive` otherwise (FR-006, FR-007).
- [X] T012 `McpTool : ITool` — the `<server>_<tool>` name, the server's description, the schema
  verbatim, the argument-object check, and the `CommandRun` effect for a destructive call (FR-005,
  FR-007, FR-008).
- [X] T013 `FlattenContent`: text, image, audio, resource with text and with blob, resource link, an
  unknown type, and the empty list; joined with a blank line and bounded with `TextLimits.Bound`
  (FR-009, FR-010).
- [X] T014 `McpToolHost`: one connection per command line in flag order, `Tools`, `Servers`,
  `StartAsync`, the idempotent `Started()` the registry factory uses, `Verbose` (the stderr sink the CLI
  sets under `--verbose`), and disposal of every connection (FR-012, FR-015).

## Phase 4 — the wiring

- [X] T015 `SharpCoreAgentOptions.McpServers`, neither persisted nor read from the environment
  (FR-001, FR-002).
- [X] T016 `ServiceCollectionExtensions.AddMcp`, called only when a server was named, and the registry
  factory that appends the host's tools to the built-ins (FR-002).
- [X] T017 `PromptLibrary.McpRule` and `SystemPromptFor(indexAvailable, webAvailable, mcpAvailable)`
  (FR-016).
- [X] T018 `CliOptions.McpServers`, `--mcp`, `RejectMcpFlags` on `models`/`index`/`config`/`setup`
  (FR-001, FR-002).
- [X] T019 `CliHost.TryStartMcpAsync` and the two commands that call it before resolving the agent, so a
  broken server is exit 2 with the reason instead of an exception (FR-012).
- [X] T020 The `mcp` row in the `chat` banner, naming the server and tool counts and staying silent
  under `--quiet` (FR-017).

## Phase 5 — the evidence

- [X] T021 `JsonRpcPipeTests` against an in-memory duplex pipe: response correlation, a notification
  ignored, a server request answered `-32601`, the malformed-line log, a timeout naming the method, the
  caller's cancellation, and a closed pipe failing the waiter.
- [X] T022 `McpProtocolTests` at the connection level, over a scripted in-memory server: the
  `initialize` payload, the `notifications/initialized` order, `tools/list` pagination, `tools/call`
  forwarding the arguments byte for byte, `isError`, a JSON-RPC error, and a server that closes mid-call.
- [X] T023 `McpToolNameTests`: folding, the 64-character cap, the repeated-stem suffix, the repeated
  tool name, and two different servers staying distinct.
- [X] T024 `McpToolTests`: `content[]` flattening for every kind, the empty list, the bound and its
  truncation flag, `isError`, the argument-object check, the safety mapping and the schema fallback.
- [X] T025 `McpOptionTests`: the default, both turn commands, the order of repeated servers, the four
  commands that refuse the flag, the environment variable that changes nothing, and `config set mcp`
  refused.
- [X] T026 `McpRegistrationTests`: no MCP tool and no prompt rule without the flag, the rule with it,
  and the host absent from the container.
- [X] T027 `McpProcessTests` against the test server that lives in this repository: the launch path, a
  real handshake, a real `tools/call`, stderr not reaching stdout, and dispose killing the child.
- [X] T028 Confirm the existing suite passes untouched: no existing assertion edited.

## Phase 6 — the surface

- [X] T029 `HelpText`: the `--mcp` row, the paragraph that says it is not a setting, and an example.
- [X] T030 `README.md`: the flag, what it enables, the naming rule and the suite count the run printed.
- [X] T031 `specs/001-coding-agent-core/contracts/cli.md` and `contracts/tools.md` for the flag and the
  server-provided tools; `specs/003-terminal-console-agent/tasks.md` T040 closed; the roadmap's round 3
  section marked as built.
- [X] T032 `PublicAPI.Unshipped.txt`: nothing to add, because every new public type is in
  `SharpCoreAgent.Infrastructure` or `SharpCoreAgent.DependencyInjection`, neither of which carries a
  snapshot.
- [X] T033 `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings and `dotnet test` green.

## Deliberately not in this round

- [X] T034a Server sampling (`sampling/createMessage`). **Closed.** `--mcp-sampling deny|first|unrestricted`
  decides whether a server may spend the session's model budget; deny is the default and answers `-32601`.
  `first` admits one request per connection and `unrestricted` uses the ceilings in `McpClientOptions`
  (10 calls and 100 000 input tokens per minute, 25 000 output tokens per call). Only text content is
  accepted, the session's current model answers, and the limiter survives a restart. `McpSamplingTests`
  proves the policy, the ceiling and the text-only rule; `JsonRpcPipeTests` proves the result and refusal
  envelopes.
- [X] T034b MCP resources and prompts. **Closed.** A server that advertises `resources` gets a
  `<server>_resources` tool that lists its catalog with no argument and reads one URI with `uri`; a
  server that advertises `prompts` gets `<server>_prompts`, which lists names and required arguments and
  renders one with `name` plus an `arguments` object. Both are read-only, use the connection's
  supervision and result ceiling, describe binary resources instead of writing them, and are absent
  when the capability was not advertised. `McpCatalogTests` proves the bridges over a scripted server;
  `McpProcessTests` proves the host adds exactly those two tools when a real server advertises them.
- [ ] T035 Remote transports (HTTP+SSE, streamable HTTP).
- [ ] T037 A declarative server list read from a file.

## Amendment — T036 supervision (2026-11-05)

- [X] T036 Server mode, reconnection and supervision. **Closed.** Server mode is the `mcp serve` surface
  `013` already ships; this amendment adds supervision to the client. A server that exits is restarted
  on the next call (never during the failed call), at most `McpClientOptions.MaxRestarts` times
  (default three), with a fresh handshake and `tools/list`. `McpServerConnection.RestartCount` and
  `IsRunning` expose the state; `McpProcessTests` proves a kill between calls, a crash mid-call with no
  replay, the restart ceiling, and the off switch. A stream-backed connection cannot restart and says so.

