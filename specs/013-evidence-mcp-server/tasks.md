# Tasks — 013 Evidence over MCP (`mcp serve`)

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the next pass
has a real list rather than a memory.

The phases below are the six phases of [plan.md](plan.md), and each closes with the gate that plan names. The
published set is `search_code`, `find_symbol`, `find_references`, `code_context`, `index_status` — five names,
one allow-list, no flag (FR-009, FR-010).

## Phase 1 — the protocol, before any tool

- [X] T001 `McpServerOptions`: the three names (`sharpcoreagent`, the assembly version, the protocol version),
  the versions this build echoes back, the refresh switch and its two budgets (3 s, 5 s), and a `Validate()` that
  refuses a fallback version it does not know (FR-004, FR-012). As built: `KnownProtocolVersions` is
  `2024-11-05`, `2025-03-26`, `2025-06-18`, and the fallback must be a member of it — a fallback outside the set
  is a version the server would claim and not implement.
- [X] T002 `StdioRpcWriter`: UTF-8 without a BOM, one serialized message per line, flushed per write, and one
  write gate so two answers cannot interleave (FR-003). As built: no newline guard. `JsonNode.ToJsonString()`
  escapes control characters, so a guard could never fire, and a guard that cannot fire is a guard whose test can
  only assert a lie. What ships instead is the test that matters — a five-line tool result still occupies exactly
  one line and round-trips with its newlines intact.
- [X] T003 `McpServerSession`: one reader, one request at a time in arrival order, `initialize`, `ping`,
  notification silence, and the four error codes with their ids (FR-003, FR-004, FR-007, FR-008, FR-015, FR-016).
  As built: the loop takes the caller's cancellation token, ends on stdin EOF, and returns 0 for both; a
  `McpTransportException` from the writer ends the loop rather than failing the process, because a client that
  stopped reading cannot be told anything.
- [X] T004 `McpServerSessionTests`, over two memory streams: the echoed version, the fallback for an unknown one,
  `initialize` with no `protocolVersion` at all, `ping` → `{}`, `-32601` for `prompts/list` with the loop still
  running, `-32700` with a null id, `-32600` for a body that is not an object, `-32600` keeping the id of a
  request with no method, two notifications producing no answer, blank lines ignored, and answers in arrival
  order.
- [X] T005 `McpServerStdoutTests`: one answer is one line, no byte-order mark, no carriage return, and a
  multi-line tool result stays a single line (FR-003).

Closes with: `dotnet build` and the fourteen session tests green on a zero-warning build.

## Phase 2 — the published set

- [X] T006 `McpServerPublishedSet`: the five names in a fixed order, resolved against the registry as
  `Safety == ToolSafety.ReadOnly` ∩ allow-list. A name on the list that is absent from the registry is skipped,
  not published from a stale schema (FR-009).
- [X] T007 `tools/list`: one page, no `nextCursor`, `name` + `Description` + `ParametersSchemaJson` byte for
  byte + `annotations.readOnlyHint: true`; an explicit `cursor` is ignored rather than refused (FR-005).
- [X] T008 `tools/call`: lookup in the published set, `arguments` forwarded verbatim (absent means `{}`), a
  `ToolInvocationContext` whose `Approval` is `ReadOnly`, and the answer shaped
  `{"content":[{"type":"text","text":"…"}],"isError":true?}` with the tool's own text unaltered (FR-006, FR-011).
- [X] T009 `-32602` for an unpublished name and for `params` that are not an object; the session survives both
  (FR-007).
- [X] T010 `notifications/cancelled` cancels the call whose id it names, and only that call (FR-008).
- [X] T011 Tests: a registry-wide assertion that `write_file`, `delete_file`, `replace_in_file`, `run_shell`,
  `shell_output`, `shell_stop`, `list_directory`, `read_file`, `search_text`, `list_roots`, `fetch_url` and every
  `McpTool` are absent; schema byte-equality; `isError` present only when true; both `-32602` shapes; and a
  cancelled call answering nothing (FR-006, FR-007, FR-009, FR-010). — `McpServerPublishedSetTests` (18) and
  `McpServerToolsTests` (11).

## Phase 3 — the process

- [X] T012 `AddMcpServer` in `ServiceCollectionExtensions`, constructing the server from the workspace alone —
  no chat model, no key, no HTTP handler (FR-001). As built: the five tools are registered here rather than
  borrowed from `AddSharpCoreAgent`, because that surface includes the ones that write and start processes, and
  the absence has to be a property of the registration rather than of a code path. `McpServerCompositionTests`
  asserts the container, not the flag: no `IChatModel`, no `IChatModelResolver`, no `HttpClient`, no `McpToolHost`.
- [X] T013 Lease-per-call: the index handle is opened for one `tools/call` and released after it, so a server held
  open does not block `sharpcoreagent index` in another process (FR-014). Verified from a second process during
  the phase, not afterwards. As built: `LeasedCodeIndex` opens per operation and disposes in a `finally`, so a
  failure and a cancellation release the handle too; the refresh opens its own maintainer per attempt for the same
  reason. Measured: with `mcp serve` running and holding a call in its history, `sharpcoreagent index` in a second
  process finished in 294 ms (1 file, 1 unchanged) and the next call still answered.
- [X] T014 Refresh gate before `tools/call` only: `EnsureFreshAsync(3 s)`, at most once per 5 s, off under
  `--no-refresh`, and a failed refresh still answered from the index that is there (FR-012, FR-013). As built:
  `IMcpCallGate` is a port the session runs before a call and before nothing else, so `initialize`, `ping` and
  `tools/list` stay at handshake speed. The interval is stamped before the attempt, so a refresh that fails is
  not retried by every call that follows it. `IndexRefreshGateTests` moves the clock instead of sleeping.
- [X] T015 `mcp` command and `mcp serve` wiring: `CliCommand.Mcp` with a `McpAction`, `--workspace` reused,
  `--no-refresh` the only new flag, `--web`/`--web-allow`/`--mcp`/`--provider`/`--model`/`--base-url`/`--fallback`
  refused with exit 2, and the ready line on stderr rather than stdout (FR-001, FR-003, FR-018). As built: the
  refused list also covers `--session`, `--add-root`, `--json` and `--no-index` — the last one because every
  published tool reads the index, so accepting it would leave the server with nothing to answer from while the
  developer believed it had stopped walking the tree. The typed flags are checked, not the merged values: an
  exported `SHARPCOREAGENT_PROVIDER` belongs to the shell. `McpCommand` takes its two streams as parameters, so
  `McpServeCommandTests` runs the container, the ready line, the loop and the exit code with no child process.
- [X] T016 Ctrl+C and stdin EOF cancel an in-flight call and exit 0; an unexpected failure exits 1, with the
  stack trace only under `--verbose` (FR-016). As built: `McpServerSession.RunAsync` returns 0 for end of input,
  cancellation and a client that stopped reading; `McpCommand` maps an unusable `--workspace` to 2 before any
  protocol byte is written, and anything else travels to `Program`, which is where exit 1 and the `--verbose`
  stack trace already live. Verified in the child process: closing stdin exited 0.
- [X] T017 Tests: the refresh budget and its interval, a refresh that cannot run, the cross-process lock check,
  and the usage errors. As built: `IndexRefreshGateTests` (8), `LeasedCodeIndexTests` (5, including one real build
  against a live lease), `McpServerCompositionTests` (4), `McpServeParsingTests` (23) and `McpServeCommandTests`
  (6). The cross-process check is the measurement in T013 rather than a test that would need the CLI's own
  runtimeconfig copied next to the test assembly; the lock it exercises is already covered by `WriterLockTests`.

## Phase 4 — the snippet

- [ ] T018 `mcp config --client <claude|vscode|codex>`: three shapes — `.mcp.json`, `.vscode/mcp.json`, a Codex
  `config.toml` block — each naming the absolute path of the running binary and `mcp serve`, printed and exited 0
  (FR-002).
- [ ] T019 `HelpText`, `README.md`, `specs/001-coding-agent-core/contracts/cli.md` and
  `contracts/mcp-server.md` kept in step with what shipped (FR-018).
- [ ] T020 Tests: the three blocks parsed back as JSON/TOML, the `--client` value required and validated, and the
  help golden test.

## Phase 5 — the evidence

- [ ] T021 Dogfood test: this product's `--mcp` client starts `mcp serve` on a fixture workspace, sees
  `sharpcoreagent_search_code`, calls it, and gets the same text the in-process tool returns — no key, no network
  (FR-017).
- [ ] T022 `mcp bench`: the twelve fixture queries, the evidence-to-source ratio of SC-006 and the latency of
  SC-005, sharing the tool code path rather than a script that reimplements it (FR-019).
- [ ] T023 Run it once and paste the numbers into `plan.md`, including a bad result.

## Phase 6 — the round

- [X] T024 Constitution 1.2.0: principle II gains the served direction (`ReadOnly` ∩ allow-list, nothing that
  writes or reaches the network), principle VIII says serving adds no key and no connection, and the migration
  note records that no default changed.
- [X] T025 Roadmap: round 6 accepted 2026-10-28, the round-6 row and the "What stays out of this program" paragraph
  updated to say the exclusion was reversed and why, and the constitution marked as a dependency met.
- [ ] T026 README section with the three client snippets and the "run `index` once after cloning" note; suite
  count in `plan.md`; merge `--no-ff`.
