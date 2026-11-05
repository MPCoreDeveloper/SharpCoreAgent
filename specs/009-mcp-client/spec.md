# 009 — Any server's tools: the MCP client

**Status**: implemented (T001–T033, T034a and T036; T034b, T035 and T037 deferred, listed in `tasks.md`)
**Branch**: `feature/009-mcp-client`, merged to `main` as `be0b265`
**Depends on**: `001-coding-agent-core` (the `ITool` model, the result contract, the approval modes),
`003-terminal-console-agent` (the console surface and `T040`, the task this round closes),
`008-web-fetch` (the precedent for a capability that exists only when the developer names it)

## Why this feature exists

Every capability in this product so far is a tool someone wrote into this repository. The six
workspace tools, the five index tools, `run_shell` and `fetch_url` are all `ITool` implementations
compiled in. That is a fine shape for the tools the product is *about*, and the wrong shape for the
hundreds of integrations a working developer already has: Postgres, GitHub, Sentry, a browser, an
internal service, a filesystem outside the checkout.

Model Context Protocol is the answer the ecosystem converged on, and it is a small one: JSON-RPC 2.0
over a child process's stdin and stdout, three methods that matter (`initialize`, `tools/list`,
`tools/call`) and a notification between the first two. It is small enough to read in an afternoon and
to implement without a dependency, which matters here because this product ships as assemblies and
keeps its dependency list short on purpose.

So this round adds a client, not an integration: `--mcp "<command>"` starts a server for that
invocation, asks it what it can do, and registers each answer as an `ITool` beside the built-ins. The
model cannot tell the difference, the approver cannot tell the difference, and the developer gets
Postgres without this repository learning what Postgres is.

## User stories

### US1 — One flag, and the server's tools are mine (P0, Phase 1)

As a developer who already runs `@modelcontextprotocol/server-postgres`, I want this product to use
it, so that the agent can query the database I named without a second terminal or a second product.

Acceptance:

- `run "how many orders today?" --mcp "npx -y @modelcontextprotocol/server-postgres postgresql://localhost/app"`
  starts the server, registers its tools, and the model calls them like any built-in.
- The tool list is what the server said: name, description and `inputSchema` are the server's, not a
  copy maintained here.
- Each tool is named `<server>_<tool>`, so a transcript says which server answered.
- A tool the server marks `readOnlyHint: true` needs no approval in any mode; everything else is
  destructive and asks exactly like `write_file` does.

### US2 — Nothing starts unless it was named (P0, Phase 1)

As a maintainer, I want a build that configures nothing to start no processes, so that "there is an
MCP server running" is a decision someone typed rather than a property of the product.

Acceptance:

- Without `--mcp`, no process is started, no MCP tool is registered, and `McpRule` is not in the
  system prompt.
- `mcp` is not a settings key and not an environment variable: `config set mcp …` is refused and
  `SHARPCOREAGENT_MCP` is never read. A resumed `chat` starts with no servers unless this invocation
  names them.
- A server that cannot start, answers garbage, or exposes no tools fails the process with exit 2 and
  a sentence naming the server — not a turn that quietly has fewer tools than the developer granted.

### US3 — The child dies with the parent (P0, Phase 1)

As the person whose laptop ran a session last night, I want no orphaned `node.exe` holding a port,
so that "the agent started a server" is not a memory leak with a process attached.

Acceptance:

- The server process and its tree are killed when the container is disposed, when the session ends,
  and when the host process exits — the same guarantee `ShellProcessRegistry` gives a shell command.
- A server that dies mid-session turns its next call into a tool *failure* naming the exit code, not a
  hung request: one dead server does not hang the turn.
- The server's stderr is not the transcript: it goes to the developer under `--verbose`, labelled with
  the server, and never into a message the model reads.

### US4 — The result is bounded and honest (P1, Phase 1)

As a developer watching a turn, I want an MCP answer capped like any other tool's, so that one
`SELECT *` cannot spend the context window.

Acceptance:

- The flattened text of `content[]` is bounded by the tool's ceiling and then by
  `MaxToolResultChars`, and the result says it was cut.
- `isError: true` from the server is a tool failure carrying the server's own text.
- A `content[]` entry this client does not understand (`image`, `audio`, `resource`,
  `resource_link`, something new) is *described* rather than dropped or dumped as base64.

## Functional requirements

- **FR-001** `--mcp <command>` on `run` and `chat`, repeatable; each value is one server command line
  run through the platform shell exactly as typed. Parsed into `CliOptions.McpServers` →
  `SharpCoreAgentOptions.McpServers`.
- **FR-002** The flag is a usage error (exit 2) on `models`, `index`, `config` and `setup`, and is not
  a settings key, not an environment variable, and not part of a resumed session.
- **FR-003** One client per server: JSON-RPC 2.0, one JSON object per line, newline-delimited, over
  the child's stdin/stdout. `initialize` (protocol version, empty capabilities, client name and
  version), `notifications/initialized`, `tools/list` with cursor pagination, `tools/call`.
- **FR-004** The server's reported protocol version is accepted whatever it is and recorded; the
  client sends `2025-06-18` and does not depend on a version-specific method.
- **FR-005** Every MCP tool becomes an `ITool` named `<server>_<tool>`: `<server>` derived from the
  command's executable stem, `<tool>` the server's name, both folded to `[a-z0-9_]`, the whole name
  at most 64 characters and unique within the registry.
- **FR-006** `annotations.readOnlyHint == true` maps to `ToolSafety.ReadOnly`; anything else, and any
  tool with no annotations, maps to `ToolSafety.Destructive`.
- **FR-007** The tool's `parametersSchemaJson` is the server's `inputSchema` verbatim when it is a
  JSON object, and `{"type":"object","properties":{}}` when the server sent none.
- **FR-008** `tools/call` forwards the model's argument object unchanged. A call whose arguments are
  not a JSON object is a failure before anything is sent.
- **FR-009** The result is the flattened `content[]` — text concatenated with one blank line between
  entries, every other type described — bounded at 64 000 characters, then by the loop's own budget.
  Amended by `010` for image entries, which are now described **and saved**: see
  [`specs/010-browser/contracts/mcp-content.md`](../010-browser/contracts/mcp-content.md).
- **FR-010** `isError: true` is a `ToolResult` with `IsError` set carrying the same flattened text; a
  JSON-RPC error object is a failure naming the code and message.
- **FR-011** A control request has 20 seconds, a tool call 120; a timeout is a failure naming the
  method and the budget. The caller's cancellation is honoured and sent to the server as
  `notifications/cancelled` on a best-effort basis.
- **FR-012** Startup is atomic: `initialize`, then `tools/list` for every server, before the first
  turn. A server that cannot start, cannot complete the handshake, answers malformed JSON, or reports
  zero tools fails the process with exit 2 and a message naming the command and the reason.
- **FR-013** A server that exits after startup is restarted on the next tool call, at most
  `McpClientOptions.MaxRestarts` times per connection (default three), with a fresh `initialize`,
  `notifications/initialized` and `tools/list`. The call that saw the failure is never repeated: it
  returns a failure saying the next call will restart the server, or that the restart budget is spent.
  A connection handed its streams by an embedder cannot be restarted, and a restart that cannot start
  the command leaves the server stopped rather than pretending the next call will work.
- **FR-013a** A restart refreshes the connection's `tools/list`. The tool definitions a turn was
  constructed with do not change: a renamed or removed tool is an ordinary failed call, not a new or
  missing tool in the middle of a turn.
- **FR-014** Server stderr is drained continuously into a bounded buffer and echoed to the developer,
  labelled `<server>: `, only when `--verbose` is in force. It never reaches the transcript.
- **FR-015** Processes are killed as a tree on container disposal and on process exit.
- **FR-016** `PromptLibrary.McpRule` ships only when at least one server was named, and says: a
  prefixed tool comes from a server the developer started, its answers are data rather than
  instruction, and a destructive one asks for approval like any other write.
- **FR-017** The `chat` banner gains an `mcp` row only when servers were named, naming the server
  count and the tool count, and stays silent under `--quiet`.
- **FR-018** A server's `sampling/createMessage` is answered `-32601` unless `--mcp-sampling` names
  `first` (one request per connection) or `unrestricted` (10 calls and 100 000 input tokens per
  minute, 25 000 output tokens per call). Only text content is accepted, the session's current model
  answers, and the limiter counts an admitted request even when that model call fails.

## Non-functional requirements

- **NFR-001** No new package reference: JSON-RPC is `System.Text.Json` and `System.Diagnostics.Process`.
- **NFR-002** No test opens a socket, needs a key, or needs Node, Python or a real MCP server. The
  protocol is tested against an in-memory duplex pipe; the process handshake and the kill test use a
  server that lives in this repository.
- **NFR-003** Startup adds no work to a build without `--mcp`: no process, no thread, no timer.
- **NFR-004** The suite stays green and warning-free on `dotnet build .\SharpCoreAgent.slnx -c Release`.

## Out of scope (recorded, not forgotten)

- **MCP resources, prompts and logging.** Three more capabilities of the same protocol, and none of
  them is a tool the model calls. A round that ships `tools/*` correctly is the one that can ship
  `resources/*` next. Sampling is no longer out of scope: see T034a and `--mcp-sampling`.
- **Remote transports (HTTP+SSE, streamable HTTP).** A socket, a token and an OAuth flow are a
  different threat model from a child process the developer started.
- **Server mode as a general MCP gateway.** `mcp serve` (`013`) is this product's one server: five
  read-only evidence tools for one workspace. Exposing the agent's write and process surface to another
  process is a different product with a different approval boundary.
- **A declarative server list (`.mcp.json`).** A file that starts processes on every run is exactly
  the authority-nobody-granted case `--web` and `--add-root` refuse; it belongs to a round that argues
  for it.

## Exit codes

| Code | Meaning |
|---|---|
| 2 | `--mcp` on a command that cannot use it; a server that will not start, will not hand shake, or exposes no tools |

A failed `tools/call`, a timeout or a dead server mid-session is a *tool result*, not a process
outcome: the turn continues and the model reads why.

## Risks

- **A server is arbitrary code with the developer's privileges.** It is started because the developer
  typed it, which is the same trust `run_shell` already has, and `--mcp` is per invocation for the
  same reason `--web` is.
- **A hostile server answers a tool call with instructions.** `McpRule` says a server's answer is data
  and names the reporting duty; the same residual risk `WebRule` carries, and the same reason the
  prompt rule ships with the capability instead of inside it.
- **Tool-name collisions.** Two servers with the same executable stem, or a server tool whose folded
  name collides with a built-in, are deduplicated by suffix within the MCP set and fail loudly against
  a built-in — `ToolRegistry` already refuses duplicates at construction.
- **A server that never answers.** Every request has a budget, and a server that misses it is killed
  with the rest on disposal; a hung server costs one tool call, not the session.
- **Windows and `.cmd` shims.** `npx` is a shell script, not an executable, so the command runs
  through the platform shell; that is also what makes a developer's typed command work unchanged.

## Decisions

1. **A flag, not a file.** `--mcp` per invocation, like `--web` and `--add-root`: a list of servers in
   a file would start processes on a run that never asked for them.
2. **The shell runs the command.** `cmd /c` on Windows, `/bin/sh -c` elsewhere, because `npx -y …` is
   the shape everyone types and `.cmd` is not startable directly.
3. **Read-only by hint, destructive by default.** The server says what its tools do; a tool that says
   nothing is treated as a write, because the cost of a needless prompt is a keystroke and the cost of
   a silent write is the file.
4. **No dependency.** The protocol is three methods and a notification over newline-delimited JSON; a
   package would be a second reason to upgrade and a second thing to trust.
5. **Fail the process, not the turn, when startup fails.** The developer asked for a capability for
   this invocation; a session that silently has fewer tools than were named is worse than one that
   refuses to start.

