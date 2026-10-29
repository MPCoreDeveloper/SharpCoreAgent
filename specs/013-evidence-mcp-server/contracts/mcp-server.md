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
`search_code` (ordinal, which is also `ToolRegistry`'s order).

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

`sharpcoreagent mcp config --client <claude|vscode|codex>` prints the block below for the running binary.
The exact file each agent reads is verified against that agent's documentation when the snippets ship; what
is contractual here is only that the command exists and names an absolute path.

```json
{ "mcpServers": { "ariadne": { "command": "<path to sharpcoreagent>", "args": ["mcp", "serve"] } } }
```

## Ready line (stderr, not protocol)

An illustrative shape only — the counts are whatever the workspace reports:

```
sharpcoreagent mcp serve: /path/to/workspace · index fresh (<files> files, <passages> passages) · 5 tools
```

A client may log it. Nothing depends on it, and its text is allowed to change.
