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
  (T021 later did copy it, because the dogfood test's whole point is the process boundary — see below.)

## Phase 4 — the snippet

- [X] T018 `mcp config --client <claude|vscode|codex>`: three shapes — `.mcp.json`, `.vscode/mcp.json`, a Codex
  `config.toml` block — each naming the absolute path of the running binary and `mcp serve`, printed and exited 0
  (FR-002). As built: `McpAction.Config` joined `Serve`, and `McpCommand` answers it without opening a workspace or
  touching the index — a developer wiring up a client should not have to build an index first. `McpClientSnippets`
  renders the block: JSON through `JsonNode` so a Windows path arrives escaped, TOML with a literal string so the
  same path needs no escaping at all. The command is `Environment.ProcessPath`, and when that is the .NET host the
  entry assembly is named first, so the snippet runs under a debugger too; on this build the apphost form printed
  `command = '<…>\sharpcoreagent.exe'`, `args = ['mcp', 'serve']`, and starting exactly that served `initialize`
  and `tools/list`. The file each block belongs in is named on stderr, not in the block, so
  `mcp config --client vscode > .vscode/mcp.json` writes a file the agent can read. The entry is filed as
  `sharpcoreagent`, which is what `serverInfo` answers and what FR-017 expects the tools to be called.
- [X] T019 `HelpText`, `README.md`, `specs/001-coding-agent-core/contracts/cli.md` and
  `contracts/mcp-server.md` kept in step with what shipped (FR-018). As built: the usage block gained the verb and
  the `--client` row, the `Serve` section gained the second verb and a paragraph on which file each client reads,
  and the examples gained the redirect form. `contracts/mcp-server.md` now carries the three blocks, the scope of
  each file, the date the three shapes were checked against `docs.claude.com` (project-scope `.mcp.json`,
  `mcpServers`), `code.visualstudio.com` (`servers`, typed stdio entries) and `developers.openai.com`
  (`[mcp_servers.<name>]`), and the reason the portable block and the VS Code one are not interchangeable. The
  README gained `### Serving it to another agent` under the index section, plus the `--no-refresh` and `--client`
  rows its option table was missing.
- [X] T020 Tests: the three blocks parsed back as JSON/TOML, the `--client` value required and validated, and the
  help golden test. As built: `McpConfigCommandTests` (7) parses the two JSON blocks with `JsonDocument` and the
  TOML by its three lines — the repository has no TOML reader, and adding one to check three keys would be a
  dependency kept for a fixture — and asserts that the file each block names exists and that the arguments end in
  `mcp serve`. `McpConfigParsingTests` (24, three of them theories) covers the required client, the unknown
  client, the operand after `config`, `--client` refused on the other six commands and on `serve`, `--no-refresh`
  and `--no-index` refused here, and exit 2 with an empty stdout. `HelpTextTests` gained the verb, the four columns
  of the new row and `TheServeSectionIsGolden`, which compares the section verbatim — the plan's help golden test.
  Suite: 1070 passing.

## Phase 5 — the evidence

- [X] T021 Dogfood test: this product's `--mcp` client starts `mcp serve` on a fixture workspace, sees
  `sharpcoreagent_search_code`, calls it, and gets the same text the in-process tool returns — no key, no network
  (FR-017). As built: `Infrastructure/McpServerDogfoodTests` (2) starts the real CLI — a new `CopyProductCli`
  target in the test project copies the CLI's whole output to `$(OutDir)Cli`, so the child is the binary this
  build produced and the launch is one a user could type. The first test reads the five names off `McpToolHost`
  and asserts `sharpcoreagent_<tool>`; the prefix is the executable's stem, which is why the snippet names the
  apphost and not `dotnet <dll>`. It also asserts the server's own `mcp serve:` line arrived on stderr, so the
  child is the command rather than a stand-in. The second test compares `servers[0].CallAsync("search_code")`
  with the `SearchCodeTool` resolved from the container `mcp serve` builds (`AddMcpServer`, `Refresh = false`)
  and finds the text equal character for character (SC-002), then asserts the `McpTool` adapter a turn calls
  returns that same text, so the hop and the wrapper both add nothing. The index is built by the test and the
  child runs `--no-refresh`, so a broken index fails on the evidence (`src/Thermometer.cs` must be cited) rather
  than hiding behind a build. Suite: 1072 passing.
- [X] T022 `mcp bench`: the twelve fixture queries, the evidence-to-source ratio of SC-006 and the latency of
  SC-005, sharing the tool code path rather than a script that reimplements it (FR-019). As built: `McpBench`
  in `Commands/`, reached through `McpAction.Bench`; it refuses an operand, `--no-index`, `--no-refresh` and
  `--client`, and its questions come from `bench/queries.txt` — twelve questions a developer would ask of this
  repository, `#` for a comment — read relative to `--workspace`. Each query is asked twice: in process through
  the `search_code` the container `mcp serve` builds, and over stdio through `McpToolHost` on exactly the command
  line `mcp config` prints, with `--workspace` pinned and `--no-refresh`. The two texts are compared as strings;
  a difference is named on stderr and exits 1 rather than becoming a column. The ratio is answer characters over
  the characters of the distinct files the answer's block headings cite, counted whole and once each; the
  latency is the p95 of the stdio call by nearest rank, which with twelve samples is the slowest one. stdout is
  one summary line plus one row per query — the artefact T023 pastes — and stderr carries the header naming the
  index and every parity failure. A workspace with no built index is refused with the command that builds one,
  because twelve identical refusals would be a measurement of nothing. The plan's `[--json]` was dropped and
  plan.md says so: the table is what gets quoted, and the option table has no room to name a fourth command for
  that flag. Tests: `McpBenchTests` (9 — question parsing, cited-file accounting, the ratio, the percentile, an
  empty run, the table's shape, a real end-to-end run through the copied CLI, a missing questions file, a missing
  index), `McpServeParsingTests` (3 more, and the unknown action is now `demo`), `HelpTextTests` (the usage line,
  the verb in the Serve section, and the golden section extended). A new `CliBinary` helper names the copied CLI,
  and the dogfood test uses it too. Suite: 1085 passing.
- [X] T023 Run it once and paste the numbers into `plan.md`, including a bad result. As built: the run is in
  `plan.md` under "What it printed (2026-10-29, this repository)" — 606 files, 12 420 passages, 233.4 MiB index,
  `ratio 0.017` (138 723 characters of evidence for 8 134 414 of cited source, about 1/59th) and `p95 4425 ms`
  against SC-005's 300 ms budget, so SC-006 is met and SC-005 is missed by an order of magnitude. The bad results
  are published as such: the latency one, with the `--verbose` split showing in-process and stdio within a few
  hundred milliseconds of each other (so the hop is free and the *call* is the three seconds — the per-call lease
  on a 233 MiB index, which is the price Complexity Tracking item 3 said would be measured); and the ratio on a
  two-file fixture, `3.427`, which says the claim only means something at repository scale. The plan's `[--json]`
  is recorded as dropped, with `--verbose` in its place. One observation is left for round 002: an incremental
  `index` run crashed with `Index was outside the bounds of the array.` and `--rebuild` (841 s) repaired it; it is
  filed as `002` **T035** so it is a task with an owner rather than a sentence in a closed round's plan. T035 has
  since landed: the cause was the engine naming row ids from a counter that restarts per connection, so every
  build after the first wrote its files under ids an earlier build already held. SC-005 was restated in `spec.md`
  on 2026-10-29 with these numbers beside it, because the alternative — a cheaper per-call lease — is index work
  with a measurement of its own and belongs in `002`, where the roadmap carries it.

## Phase 6 — the round

- [X] T024 Constitution 1.2.0: principle II gains the served direction (`ReadOnly` ∩ allow-list, nothing that
  writes or reaches the network), principle VIII says serving adds no key and no connection, and the migration
  note records that no default changed.
- [X] T025 Roadmap: round 6 accepted 2026-10-28, the round-6 row and the "What stays out of this program" paragraph
  updated to say the exclusion was reversed and why, and the constitution marked as a dependency met.
- [X] T026 README section with the three client snippets and the "run `index` once after cloning" note; suite
  count in `plan.md`; merge `--no-ff`. As built: the Quickstart gained the step itself ("Build the index once per
  clone… `dotnet run --project src\SharpCoreAgent.Cli -- index`") rather than a footnote in the serving section,
  because a cloner who skips it meets four tools that refuse. The three snippets and their files were already in
  README from T019. The VS Code block was re-checked against `code.visualstudio.com` on 2026-10-29: `servers` in
  `.vscode/mcp.json` is still what the page says verbatim, and two facts the round now records instead of
  smoothing over — that destination is labelled deprecated in VS Code's guided flow, and workspace scope is being
  offered as the repository-root `.mcp.json` with `mcpServers` behind `chat.mcp.workspaceRootConfig.enabled`, which
  is the block `--client claude` already prints. `plan.md` carries the suite count (1085 passing, 0 failed, in
  Release) and its Tests table now names the files that exist: `McpServerRefreshTests` was never written — the
  refresh and lease tests are `IndexRefreshGateTests` and `LeasedCodeIndexTests` — and `McpCommandParsingTests`
  became `McpServeParsingTests`, `McpConfigParsingTests` and `McpConfigCommandTests`, with `McpBenchTests` added.
  The round closed with `dotnet build` and `dotnet test` on the solution in Release: 0 warnings, 1085 passing.
  The merge is the one item that is not as written: rounds 007–009 each landed as a `--no-ff` merge commit, but
  this round's phases went to `main` as they were produced, so there is no branch to merge and no merge commit in
  the history. Recorded here rather than manufactured. The Release gate also caught a flake that had nothing to do
  with the bench: `McpServeParsingTests` and `McpConfigParsingTests` use `ConsoleScope`, which redirects the
  process's console, but neither had joined the `CliConsole` collection that serialises the five other classes doing
  the same — so a parallel class could capture or steal the output, and one run in three failed on a message that
  was in a different writer. Both joined the collection; three consecutive Release runs are 1085/1085.
