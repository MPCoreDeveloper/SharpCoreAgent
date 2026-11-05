# Feature Specification: Evidence over MCP (`mcp serve`)

**Feature Branch**: `013-evidence-mcp-server`

**Created**: 2026-10-28

**Status**: Accepted 2026-10-28 (constitution 1.2.0). Round 6 of `specs/000-parity-roadmap.md`. SC-005 has been
restated three times, each with the measured number beside it in `plan.md`: 2026-10-29 and 2026-11-01 as misses,
and 2026-11-02 — after round 16 (`025-served-index-snapshot`) — as met at 248–261 ms p95. The other criteria stand
as written.

**Input**: User description: "… do we cover this? … a small MCP utility that makes a project's knowledge
and repeatable operations available to GitHub Copilot, Codex, or Claude Code … if not make a plan to do so."

## Why

`002-code-evidence-index` built the thing all of this is about: a per-workspace index that answers
"where is the passage that matches this intent", "what is this symbol and who calls it", and "what else
belongs to this cluster", and answers with evidence rather than prose — a path, a line range and a content
hash the caller can quote. Six read-only tools sit on top of it. `002` also drew one line around it:

> Out of scope: an MCP server (the index is in-process). — `002/spec.md:43`

That line was right for its round: the only consumer was our own loop, and `009` was still unbuilt. It is
the wrong line today, because of what `009-mcp-client` proved: **the interesting direction was backwards.**
We learned to call other people's servers (`--mcp "npx -y @playwright/mcp@latest"` is how round 4 gets a
browser). Meanwhile the one asset no other agent on the developer's machine has — this repository's index
— is the one asset that no other agent can call.

The cost is concrete. A developer with Copilot, Codex CLI or Claude Code open in VS Code has three agents
that start every session blind: `grep`, read whole files, guess which of nine similarly named classes
implements the interface in the stack trace. SharpAgent starts the same task with `search_code` and reads
four passages instead of forty files. The difference is not a feature of SharpAgent's loop; it is a property
of the index, and it is currently locked inside the loop.

This round publishes the index over stdio MCP and nothing else. It adds no model access, no network, no
write path and no second tool catalogue: the tools it serves are the tools that already exist, with the
schemas they already carry.

## What already exists, and is not being rebuilt

| Piece | Where | Used as |
|---|---|---|
| Index build, storage, retrieval, ranking | `SharpCoreAgent.Indexing` (SharpCoreDb single file; lexical + vector legs fused by RRF; symbol graph and communities; incremental refresh; verify and prune) | unchanged |
| Six read-only tools | `SearchCodeTool`, `FindSymbolTool`, `FindReferencesTool`, `CodeContextTool`, `IndexStatusTool`, `ViewImageTool` | published verbatim; `view_image` answers with an MCP `image` part |
| Tool declaration | `ITool.Definition` — name, description, `ParametersSchemaJson` | becomes `tools/list` `inputSchema`, byte for byte |
| Tool set and lookup | `ToolRegistry` / `IToolRegistry` | the published set is a filtered view of it |
| Protocol shapes | `JsonRpcPipe`, `McpToolInfo`, `McpContent` (client side), `tests/SharpCoreAgent.TestMcpServer` (a stdio server proving the shapes) | read as the contract to satisfy, not copied |
| Protocol version | `McpClientOptions.ProtocolVersion` = `2025-06-18` | the version this server announces |

Nothing under `src/` currently answers `initialize`. That is the whole gap.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - An agent that is not SharpAgent asks the index where something is (Priority: P1)

A developer has Claude Code (or Copilot agent mode, or Codex CLI) open on this repository and has added
the server once:

```json
{ "mcpServers": { "ariadne": { "command": "sharpcoreagent", "args": ["mcp", "serve"] } } }
```

They ask "where is a tool result trimmed to the budget". The agent calls `search_code`, receives the same
ranked blocks our own model would receive — `src/SharpCoreAgent.Application/ToolResultTrimmer.cs:1-60`
with a hash and the retrieval legs that found it — reads one of them, and answers. It never crawled the
tree and never read a file it did not need.

**Why this priority**: this is the entire round. Everything else is a property of it.

**Independent Test**: in a fixture workspace, `sharpcoreagent index` then `sharpcoreagent mcp serve`,
driven over pipes: `initialize` → `tools/list` → `tools/call search_code` → assert the text contains the
expected `path:start-end` heading. No API key, no network, no model.

**Acceptance Scenarios**:

1. **Given** an indexed workspace and a server started with `mcp serve`, **When** the client sends
   `initialize`, **Then** the answer names a protocol version, `capabilities.tools`, and a `serverInfo`
   whose name is `sharpcoreagent`.
2. **Given** the same, **When** the client sends `tools/list`, **Then** exactly the six documented tool
   names come back, each with the `inputSchema` text `ToolDefinition.ParametersSchemaJson` holds and
   `annotations.readOnlyHint: true`.
3. **Given** the same, **When** the client calls `search_code` with `{"query":"trim tool result"}`, **Then**
   the returned text is byte-identical to what the in-process tool returns for the same query on the same
   index.

---

### User Story 2 - The answer says how much to trust it (Priority: P2)

The index behind an answer may be minutes old. Every result already carries this — "index fresh · 0 stale",
or "index stale · 3 stale", or the partial-coverage note — so an agent can decide to trust a line range
without a second call. A caller who wants the index current can have it refreshed, on the server's terms
and inside a budget.

**Why this priority**: evidence that is quietly wrong is worse than no evidence; the freshness word is what
separates "the index says" from "the code says".

**Independent Test**: edit a file in the fixture workspace after indexing, call `search_code`, assert the
answer reports the stale count; then call again with refresh enabled and assert the count returns to zero.

**Acceptance Scenarios**:

1. **Given** an index that is behind the working tree, **When** any evidence tool answers, **Then** the
   heading states the freshness and the stale-file count.
2. **Given** the same, **When** refresh is enabled (the default) and the writer lock is free, **Then** the
   server brings the index up to date before answering, within the refresh budget.
3. **Given** another process is building the index, **When** a call arrives, **Then** the call still answers
   from the index it has and says `building`, and the server does not wait past its budget.

---

### User Story 3 - It cannot be made to write, run or fetch anything (Priority: P3)

The same registry holds `write_file`, `delete_file`, `run_shell` and — under `--web` — `fetch_url`.
None of them is published by `mcp serve`, under any flag, on any day. A name outside the published set is
an error, not a fallback to the registry.

**Why this priority**: a server another agent starts on the developer's behalf must not be a general door
to the machine. This is the constraint the round is allowed to be small for.

**Independent Test**: for every registered tool whose `Safety` is not `ReadOnly`, assert its name is absent
from `tools/list` and that `tools/call` on it answers `-32602`, naming the tool as unknown.

**Acceptance Scenarios**:

1. **Given** a server started with `mcp serve`, **When** the client calls `tools/call write_file`, **Then**
   the answer is a JSON-RPC error `-32602` and no file was touched.
2. **Given** a server started with `mcp serve`, **When** the client reads its stdout, **Then** every line is
   one JSON object and nothing else; diagnostics went to stderr.

---

### User Story 4 - Wiring it up is not a research project (Priority: P4)

The developer asks the product for the snippet instead of finding one in a blog post:
`sharpcoreagent mcp config --client claude|vscode|codex` prints the block, with the absolute path of the
binary that is running, ready to paste.

**Why this priority**: adoption friction, not capability. It is cheap, and it is the difference between "a
feature exists" and "a developer uses it".

**Independent Test**: assert each client's snippet is well-formed for that client (JSON for two,
TOML-shaped for the third) and names the running binary.

### Edge Cases

- **No index yet.** `initialize` still succeeds; every evidence tool answers the documented failure naming
  `sharpcoreagent index`. A server that refuses to start is a server an agent reports as broken.
- **Index corrupt, or written by a newer build.** Same shape: the tool failure names the state and the
  repair (`index --rebuild`).
- **`--workspace` names nowhere.** Usage error, exit 2, before the handshake. Nothing is served from a guess.
- **stdin closes mid-call.** The in-flight call is cancelled and the process exits 0; nothing is left running.
- **Unknown method, malformed JSON, missing `params`.** `-32601`, `-32700`, `-32602`; the loop survives all
  three, because the next line may be a good request.
- **A notification (no `id`).** Never answered. `notifications/initialized` and `notifications/cancelled`
  are expected; anything else is ignored in silence.
- **A result longer than the ceiling.** Bounded by the tool's own `MaxOutputChars` (24 000 characters)
  before it is marshalled, so the server never streams an unbounded string.
- **Two agents, one workspace.** Each gets its own process and its own connection; the index is shared
  through the file and the writer lock the CLI already defines.

## Requirements *(mandatory)*

### Functional Requirements

**The command**

- **FR-001**: `sharpcoreagent mcp serve` MUST run one stdio MCP server for `--workspace` (default: the
  current directory) until stdin closes, then exit 0. It MUST require no API key, construct no chat model
  and make no network call. `--web`, `--mcp`, `--base-url` and `--provider` MUST be refused with a usage
  error on this command, for the same reason `models` refuses `--web`: a capability that cannot apply is
  a mistake in the command line, not a no-op.
- **FR-002**: `sharpcoreagent mcp config --client <claude|vscode|codex>` MUST print a copy-paste block for
  that client, naming the absolute path of the running binary and the `mcp serve` arguments, and MUST exit 0.

**The protocol**

- **FR-003**: The server MUST speak JSON-RPC 2.0 over stdin/stdout, one JSON object per line and nothing
  else on stdout. Every diagnostic, banner or stack trace MUST go to stderr.
- **FR-004**: `initialize` MUST answer `protocolVersion` (the version asked for when it is one this build
  knows, else `2025-06-18`), `capabilities: {"tools":{}}` and `serverInfo` naming `sharpcoreagent` and the
  assembly version.
- **FR-005**: `tools/list` MUST return exactly the published set (FR-009) in one page, with no `nextCursor`;
  each entry carries `name`, the tool's `Description`, `inputSchema` equal to the tool's
  `ParametersSchemaJson` **byte for byte**, and `annotations.readOnlyHint: true`. An explicit
  `cursor` in the request MUST be ignored rather than refused.
- **FR-006**: `tools/call` MUST look the tool up in the published set, pass `arguments` verbatim as the
  tool's `ArgumentsJson`, and answer
  `{"content":[{"type":"text","text":"<ToolResult.Content>"}],"isError":<ToolResult.IsError>}`.
  `isError` MUST be present only when true, and `ToolResult.Content` MUST NOT be altered, summarised or
  re-wrapped — the caller gets the text our own model gets.
- **FR-007**: `ping` MUST answer `{}`. A request for an unknown method MUST answer `-32601`; a JSON body
  that cannot be parsed MUST answer `-32700` with `id: null`; `tools/call` naming an unpublished tool, or
  with `params` that are not an object, MUST answer `-32602`. No malformed input may end the loop.
- **FR-008**: A request without an `id` is a notification and MUST NOT be answered. `notifications/cancelled`
  MUST cancel the call whose id it names; every other notification MUST be ignored.

**What is served**

- **FR-009**: The published set MUST be exactly the tools in the registry whose `Safety` is
  `ToolSafety.ReadOnly` **and** whose name is on a fixed allow-list compiled into the server:
  `search_code`, `find_symbol`, `find_references`, `code_context`, `index_status`. The intersection is what
  ships, so a future tool that happens to be read-only cannot join the server by accident, and a flag
  cannot add one.
- **FR-010**: No flag may publish a tool that writes, deletes, starts a process or reaches the network.
  There is no `--all-tools`, so this requirement is not a default that can be overridden.
- **FR-011**: Each `tools/call` MUST run inside a `ToolInvocationContext` whose `Approval` is
  `ApprovalMode.ReadOnly`, so that even a future read-only-but-gated tool has nothing to ask.

**Freshness**

- **FR-012**: Refresh MUST be on by default: before answering `tools/call`, the server refreshes the index
  through `ICodeIndexMaintainer.EnsureFreshAsync` with a 3-second budget, and at most once every 5 seconds.
  `--no-refresh` turns it off. `initialize` and `tools/list` MUST NOT refresh.
- **FR-013**: When the refresh cannot proceed — no index, another writer holds the lock, budget exhausted,
  index corrupt — the call MUST still answer from whatever index is there, and the freshness word in the
  tool's own heading tells the caller what happened. Refresh MUST never turn a readable answer into an error.

**Footprint**

- **FR-014**: The server MUST NOT hold the index database open between calls when doing so would block a
  build in another process: the handle is leased for the length of one call and released after it.
- **FR-015**: One request MUST be handled at a time, and the loop MUST process requests in arrival order.
  Concurrency is a v2 question, and determinism is worth more than throughput on a file-backed index.
- **FR-016**: The process MUST respond to Ctrl+C and to stdin EOF by cancelling any in-flight call, closing
  the index and exiting 0.

**Proof**

- **FR-017**: The suite MUST contain a dogfood test in which this product's own `--mcp` client starts
  `mcp serve` on a fixture workspace, lists the tools as `sharpcoreagent_<tool>`, calls one and receives
  evidence — no network, no key.
- **FR-018**: `HelpText`, `README.md`, `specs/001-coding-agent-core/contracts/cli.md` and a new
  `specs/013-evidence-mcp-server/contracts/mcp-server.md` MUST describe the command, the published set, the
  protocol surface and the error codes.
- **FR-019**: A benchmark entry point MUST exist — `eng/bench-evidence.ps1` or a
  `sharpcoreagent mcp bench` action; the plan decides which — that runs the fixture queries, reports the
  ratio of SC-006 and the latency of SC-005, needs no network and no key, and is run at least once before
  the merge with its output pasted into `plan.md`.

### Key Entities

- **Published set**: the six read-only evidence tools, the allow-list, and the reason each name is on it.
- **Invocation**: one `tools/call` — id, tool name, argument object, cancellation token, `ToolResult`.
- **Ready line**: the single stderr line a client may log, naming the workspace, the index state and how
  many tools are published. Never a substitute for a protocol answer.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From a clean checkout, `index` then `mcp serve`, a client completes `initialize` and
  `tools/list` in under 2 seconds on this repository, and the list holds exactly six names.
- **SC-002**: For the same query and the same index, `tools/call search_code` over stdio returns text
  **byte-identical** to the in-process `SearchCodeTool` result. Parity is asserted, not claimed.
- **SC-003**: No tool outside the allow-list is reachable: a test enumerates the registry, filters by
  `Safety == ReadOnly`, and asserts the published set equals the allow-list exactly — so a tool added later
  fails the test rather than joining the server.
- **SC-004**: Every stdout line of a full session parses as exactly one JSON object, and a session in which
  one malformed line is injected still answers every subsequent request.
- **SC-005**: A warm `search_code` over stdio answers in under 300 ms at p95 on this repository's index,
  measured by the harness FR-019 requires, so the server is usable as an agent's first move rather than a
  step it learns to avoid.
  **Restated 2026-10-29, after the measurement** (`plan.md`, "What it printed"): the harness ran and the
  criterion was **missed** — p95 **4 425 ms** over stdio on the 606-file, 12 420-passage, 233.4 MiB index, about
  15× the budget. `--verbose` printed both halves of every query and they agree (in-process 2.6–4.6 s, stdio
  2.7–4.4 s), so the hop costs nothing measurable; the per-call index lease costs the rest, which is the risk
  Complexity Tracking item 3 named and chose to measure rather than assume. The criterion this round accepts is
  therefore: **a served call costs no more than the same call in-process** (parity, measured 12/12), and the
  300 ms budget is recorded as missed, with 4 425 ms published as the latency. Cheapening the lease is left
  open rather than claimed; the roadmap carries it as open work.
  **Re-measured 2026-11-01, on a full index** (666 files, 12 889 passages, 350.2 MiB): p95 **3 199 ms**,
  parity 12/12, evidence-to-source ratio 0.021, and the two sides differ by 6 to 283 ms on calls of
  2.5–3.2 s with neither consistently the slower one — the hop is still free and the criterion is still missed
  by about ten. The mechanism is now named rather than
  guessed: `LeasedCodeIndex` builds a new `SharpCoreDbCodeIndex` per operation, the `build_id`-keyed cache in
  `IndexReader` lives inside that instance, and every served call therefore reloads all 12 889 chunk rows with
  their text and their 512-wide embeddings before it can rank anything. Opening the same database costs 0.36 s
  end to end (`sharpcoreagent index --status`), which rules the open out as the cost. What is left is work
  rather than measurement: share one snapshot across per-call handles, and say what `warm` means in the
  harness — with twelve samples the nearest-rank p95 *is* the slowest one, and the call that pays the load is
  the first after a cold start.
  **Measured a third time, 2026-11-02, after round 16 — SC-005 is now met** (`plan.md`, "What it printed
  (2026-11-02)"): p95 **261 ms** and **248 ms** over two runs on the 712-file, 13 686-passage, 350.2 MiB index,
  parity 12/12, with the cold first call published beside it at **2 641–2 834 ms**. The work the 2026-11-01
  paragraph asked for was done, and the measurement found a second half the paragraph did not name: the *status*
  every tool reads before it answers cost 1 347–1 435 ms on a fresh instance, against 34–38 ms for the search
  itself. Both the build and the status are now held per database rather than per call. The criterion is
  restated to say which call it is about — **a warm call**, after the first — because that was always the
  sentence's meaning and the harness can now tell the two apart. The cold call is not a miss: it is published.
- **SC-006**: The evidence-to-source ratio is published as a measured number, not a projection: for a
  checked-in set of fixture queries, the characters an answer spends are compared with the characters of
  the files it cites, and the ratio is recorded in `plan.md`. There is no vendor percentage in this
  specification and none in the docs; the number this round prints is the only one it may quote.

## Assumptions

- The developer's agent can launch a stdio server: Claude Code, VS Code's MCP support and Codex CLI all
  can, and the plan verifies the exact configuration file shape for each against its own documentation
  before the snippets are written.
- `sharpcoreagent` is on `PATH` for the agent, or the snippet carries the absolute path — which is what
  `mcp config` exists to provide.
- The index is built by the developer: `sharpcoreagent index` once, then refresh keeps it warm. Nothing in
  this round builds an index it was not asked to build.
- One workspace per process. Serving several repositories from one server is a different feature and is
  not promised here.
- The published tools are the *evidence* tools, not the file tools. An agent in an editor already reads
  files; what it cannot do is rank them.

## Out of Scope

- A `read_file`/`list_directory`/`search_text` passthrough over MCP. It is a plausible `--read-tools` flag
  and it is deliberately not in this round: it would make the server a second file API, and the round's
  argument is that the index is what is missing.
- HTTP, SSE or streamable-HTTP transports; authentication; multi-workspace routing; remote or team sharing.
  (`009` T035 keeps the client half of that question open, and this round does not answer it.)
- Sampling, prompts, resources, logging, progress and subscriptions: the server advertises `tools` and
  nothing else, and answers `-32601` to the rest. `009` T034 stays open as the record.
- `web_search` (still unnumbered) and the browser (round 4). Neither becomes reachable through this server.
- Indexing a workspace the server was not pointed at, or writing the index from inside the server beyond
  the bounded refresh of FR-012.
