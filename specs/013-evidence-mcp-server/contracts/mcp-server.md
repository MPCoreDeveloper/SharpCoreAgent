# Contract: the `mcp serve` wire surface

**Feature**: `013-evidence-mcp-server` — see [../spec.md](../spec.md) and [../plan.md](../plan.md)

This is the whole surface. Anything not in the tables below is not implemented, and answers `-32601`.

## Transport and framing

- stdio. One JSON object per line on stdin, one per line on stdout. UTF-8, no BOM, no `Content-Length`.
- stdout carries protocol messages only. Diagnostics — the ready line, refreshes, stack traces under
  `--verbose` — go to stderr.
- Requests are handled one at a time in arrival order.
- The process exits 0 when stdin reaches end of input, and on Ctrl+C after cancelling any in-flight call.

## Methods

| Method | Params | Result | Notes |
|---|---|---|---|
| `initialize` | `protocolVersion`, `capabilities`, `clientInfo` | `protocolVersion`, `capabilities: {"tools": {}}`, `serverInfo: {"name":"sharpcoreagent","version":"<assembly>"}` | `protocolVersion` echoes the client's when this build knows it, else `2025-06-18` |
| `ping` | — | `{}` | |
| `tools/list` | optional `cursor` | `tools: [...]` | one page, never a `nextCursor`; a `cursor` is ignored |
| `tools/call` | `name`, `arguments` (object) | `content: [{"type":"text","text":"…"}]`, `isError` when true | `arguments` absent means `{}` |
| `notifications/initialized` | — | no answer | it is a notification |
| `notifications/cancelled` | `requestId`, `reason` | no answer | cancels the named call |

Everything else, including `resources/*`, `prompts/*`, `logging/setLevel`, `completion/complete` and
`sampling/createMessage`, answers `-32601`.

## `tools/list` entries

Each entry is (the `properties` object elided — it is the tool's own schema, verbatim):

```json
{
  "name": "search_code",
  "description": "<ToolDefinition.Description, verbatim>",
  "inputSchema": { "type": "object", "properties": { "query": { "type": "string" }, "…": { } }, "required": ["query"], "additionalProperties": false },
  "annotations": { "readOnlyHint": true }
}
```

`inputSchema` is the tool's `ParametersSchemaJson` parsed and re-emitted as an object: the caller receives the
same schema the provider receives, including `enum`, `minimum` and `additionalProperties`.

Published names, in this order: `code_context`, `find_references`, `find_symbol`, `index_status`,
`search_code`, `view_image` (ordinal, which is also `ToolRegistry`'s order). `view_image` returns its picture as
an `image` content part beside the sentence, not as a path the client would have to open.

Never published, whatever the flags: `delete_file`, `fetch_url`, `list_directory`, `list_roots`, `read_file`,
`replace_in_file`, `run_shell`, `search_text`, `shell_output`, `shell_stop`, `write_file`, and every
`<server>_<tool>` adapter from `--mcp`.

## `tools/call`

Request:

```json
{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"search_code","arguments":{"query":"trim tool result","k":3}}}
```

Success:

```json
{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"found 3 blocks (asked 3) · index fresh · 0 stale · lexical+vector+graph\n\n…"}]}}
```

Tool-level failure — the text is the tool's own sentence, and the model is expected to read it:

```json
{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"the code index is missing; run 'sharpcoreagent index'"}],"isError":true}}
```

## Errors

| Code | When | Shape of `message` |
|---|---|---|
| `-32700` | the line is not JSON | `the request is not JSON` |
| `-32600` | no `method`, or `method` is not a string | `the request has no method` |
| `-32601` | unknown method | `'<method>' is not implemented by this server` |
| `-32602` | `tools/call` for an unpublished name; `params` not an object | `unknown tool '<name>'; this server publishes <n> evidence tools` |

An error response always carries `id`, except `-32700`, which carries `"id": null` because the line had no
readable id.

## What a result never is

- Never a summary of a passage: every block is a `path:start-end` with a hash and the legs that found it.
- Never rewritten by the server: `ToolResult.Content` is passed through unchanged.
- Never larger than the tool's own ceiling (24 000 characters for the evidence tools), so the server cannot
  stream an unbounded string onto a caller's stdout.

## Client configuration

`sharpcoreagent mcp config --client <claude|vscode|codex>` prints the block for the running binary and exits 0.
It opens no workspace, reads no index and needs no key: it answers a question about configuration, and a
developer wiring up a client should not have to build an index first.

What is contractual: the command exists, the block names the absolute path of the executable that is running
(and, when that executable is the .NET host, the assembly it should load), the two `mcp serve` arguments are
last, and the block is the only thing on stdout. The line naming the file goes to stderr, so
`mcp config --client vscode > .vscode/mcp.json` writes the file and a paste still reads.

The entry is filed as `sharpcoreagent`, the name the server answers in `serverInfo` and the prefix an agent
puts in front of each tool (`sharpcoreagent_search_code`).

| Client | File | Top-level shape |
|---|---|---|
| `claude` | `.mcp.json` at the repository root — project scope, committed and shared | `mcpServers` |
| `vscode` | `.vscode/mcp.json` — workspace scope | `servers`, with `"type": "stdio"` |
| `codex` | `~/.codex/config.toml` — user scope | the `[mcp_servers.sharpcoreagent]` table |

```json
{
  "mcpServers": {
    "sharpcoreagent": {
      "command": "<absolute path to sharpcoreagent>",
      "args": ["mcp", "serve"]
    }
  }
}
```

```json
{
  "servers": {
    "sharpcoreagent": {
      "type": "stdio",
      "command": "<absolute path to sharpcoreagent>",
      "args": ["mcp", "serve"]
    }
  }
}
```

```toml
[mcp_servers.sharpcoreagent]
command = '<absolute path to sharpcoreagent>'
args = ['mcp', 'serve']
```

Two details that are not cosmetic and are why the command exists rather than one generic block: VS Code's own
format says `servers` where the portable format says `mcpServers`, and Codex reads TOML, not JSON. The TOML
path is a literal string (`'…'`), which is the one form in which a Windows separator stays a separator.

Re-checked on 2026-10-29, because this is the block most likely to drift. VS Code's page still says, verbatim,
"Portable files use a top-level `mcpServers` object. The VS Code format uses `servers`", and `.vscode/mcp.json`
is still listed as a workspace destination — with two additions the round records rather than smooths over:
that destination is now labelled deprecated in VS Code's guided flow, and workspace scope is being offered as
the repository-root `.mcp.json` (the portable `mcpServers` shape) behind the experimental
`chat.mcp.workspaceRootConfig.enabled` setting. So both blocks this command prints are current, and
`--client claude` already prints exactly the portable file VS Code is moving towards; which file to write is
decided by the table above and by the one line on stderr. Codex's `[mcp_servers.<name>]` table with `command`
and `args`, and Claude Code's project-scope `.mcp.json`, were checked the same day. Where a file is user-scope
rather than repository-scope, the block is a table to add, not a file to replace — which is what the stderr line
says.

The block does not carry `--workspace`: the agent starts the server in the directory it is working in, which is
the workspace the developer opened. Pinning another one is an `args` edit, and the help says so.

## Ready line (stderr, not protocol)

An illustrative shape only — the counts are whatever the workspace reports:

```
sharpcoreagent mcp serve: /path/to/workspace · index fresh (<files> files, <passages> passages) · 5 tools
```

A client may log it. Nothing depends on it, and its text is allowed to change.

## `mcp bench`: the measurement

`sharpcoreagent mcp bench` is the harness the round's numbers come from, and it is a client of the surface above
rather than a second implementation of it.

- **Questions** come from `specs/013-evidence-mcp-server/bench/queries.txt`, relative to `--workspace`: one per
  line, `#` for a comment, blank lines ignored. The command takes no operand, so the questions a published number
  was measured on cannot be edited on the command line.
- **Two answers per question.** Each query is asked of `search_code` in process (through the container
  `mcp serve` builds) and over stdio (through this product's own client, against a server started as
  `<this binary> mcp serve --workspace <root> --no-refresh`). The two texts are compared as strings; a pair that
  differs is a failure, named on stderr, and the run exits `1`.
- **The ratio (SC-006)** is `answer characters / characters of the distinct files the answer cites`, where a file
  is counted once per query and counted whole rather than by the cited range. Below 1 means the answer cost less
  than reading the files it points at.
- **The latency (SC-005)** is the p95 of the stdio `tools/call` wall-clock, by nearest rank: with twelve samples
  that is the slowest call, not an interpolated one. Since round 16 the run spends one untimed call before the
  samples — the first question, asked over stdio — so the samples describe a server that has already answered, and
  that call's cost is reported separately as `cold`. Without it the p95 *is* the first call by construction, and a
  warm server would measure as a cold one.
- **stdout** carries one summary line, a blank line and one row per query — that is the artefact the numbers in
  `plan.md` are a paste of. **stderr** carries the header naming the index, and every parity failure.
- It needs a built index (`sharpcoreagent index` first) and refuses to run without one, because twelve identical
  "no index" answers would be a measurement of nothing.

