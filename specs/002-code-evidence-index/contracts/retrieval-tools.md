# Contract: Retrieval Tools

**Scope**: five tools in `SharpCoreAgent.Infrastructure.Tools`, each implementing `ITool` exactly as
`specs/001-coding-agent-core/contracts/tools.md` requires, each bound to `ICodeIndex` and to nothing
storage-shaped.

These tools are the only way the model reaches the index (Principle II). None of them writes, none of
them opens a database, none of them knows what SharpCoreDB is.

## Common obligations

1. `Safety => ToolSafety.ReadOnly` for all five; on every success the result carries
   `new ToolEffect(ToolEffectKind.Read, ".")` — the index is derived from the workspace, so the target
   is the workspace, and no file was touched.
2. Arguments parsed with `ToolArgumentReader.Parse(Name, call.ArgumentsJson)`; a missing or invalid
   field throws `ToolArgumentException` naming the field and its expected form, before any query runs.
3. Output capped by the tool *and* the caller: ≤ 12 blocks (`search_code`), ≤ 50 symbols, ≤ 100
   references, `TextLimits.Bound` applied to the final string, `IsTruncated` set with
   `[truncated]` appended.
4. Deterministic: no clock, no random, no map iteration in ordering (research D13).
5. Failure is a `ToolResult.Failure`, never an exception: a missing index is
   *"the code index is missing for this workspace; run `sharpcoreagent index`"*, a disabled index is
   *"the code index is disabled (--no-index); use search_text and read_file instead"*.
6. A path argument is resolved with `Workspace.TryResolve` before it is used as a filter; the tool then
   passes only the workspace-relative form to the index.

## Catalogue

| Name | Safety | Effects | Arguments |
|---|---|---|---|
| `search_code` | ReadOnly | `Read` | `query` (required), `path?`, `kind?`, `k?` (1–12, default 8), `hops?` (0–3, default 1) |
| `find_symbol` | ReadOnly | `Read` | `name` (required), `kind?`, `max_results?` (1–50, default 20) |
| `find_references` | ReadOnly | `Read` | `symbol` (required), `direction?` (callers\|callees\|both, default callers), `max_results?` (1–100, default 40) |
| `code_context` | ReadOnly | `Read` | `target` (required: path or symbol), `hops?` (1–3, default 1), `k?` (1–12, default 8) |
| `index_status` | ReadOnly | `Read` | none |

### JSON Schemas (exact strings returned to the model)

`search_code`
```json
{"type":"object","properties":{"query":{"type":"string","description":"What to find: words, identifiers or a question."},"path":{"type":"string","description":"Limit to a file or directory relative to the workspace root."},"kind":{"type":"string","enum":["any","class","interface","record","struct","enum","method","property","field","doc","config"],"description":"Restrict to one declaration kind. Defaults to any."},"k":{"type":"integer","minimum":1,"maximum":12,"description":"How many blocks to return. Defaults to 8."},"hops":{"type":"integer","minimum":0,"maximum":3,"description":"How far to expand through the symbol graph before ranking. Defaults to 1."}},"required":["query"],"additionalProperties":false}
```

`find_symbol`
```json
{"type":"object","properties":{"name":{"type":"string","description":"Symbol name, qualified name or a fragment of one (case-insensitive)."},"kind":{"type":"string","enum":["any","class","interface","record","struct","enum","method","property","field"],"description":"Restrict to one declaration kind. Defaults to any."},"max_results":{"type":"integer","minimum":1,"maximum":50,"description":"Defaults to 20."}},"required":["name"],"additionalProperties":false}
```

`find_references`
```json
{"type":"object","properties":{"symbol":{"type":"string","description":"Exact or qualified symbol name, as returned by find_symbol."},"direction":{"type":"string","enum":["callers","callees","both"],"description":"Defaults to callers."},"max_results":{"type":"integer","minimum":1,"maximum":100,"description":"Defaults to 40."}},"required":["symbol"],"additionalProperties":false}
```

`code_context`
```json
{"type":"object","properties":{"target":{"type":"string","description":"A workspace-relative path, or a symbol name."},"hops":{"type":"integer","minimum":1,"maximum":3,"description":"How many graph hops to follow. Defaults to 1."},"k":{"type":"integer","minimum":1,"maximum":12,"description":"How many neighbouring declarations to show. Defaults to 8."}},"required":["target"],"additionalProperties":false}
```

`index_status`
```json
{"type":"object","properties":{},"required":[],"additionalProperties":false}
```

## Output formats (exact, asserted by tests)

### `search_code`

```
found 3 blocks (asked 8) · index fresh · 0 stale · lexical+vector+graph

1) src/SharpCoreAgent.Cli/Program.cs:41-58 · class CliHost · sha256:9f2c1ab4 · why: lexical,vector
    41│public sealed partial class CliHost
    42│{
```
…one block per heading, each line prefixed with its real line number and `│`. Header fields, in order:
`path:start-end`, `kind`, `symbol`, `sha256:<8 hex>`, `why: <legs>`, and `stale` when the file changed
after the build. Blocks are separated by one blank line.

- Nothing matched: `no blocks matched 'xyz' in the index (412 files, 1 908 passages)` — success, not an
  error, because "I looked and found nothing" is an answer.
- Partial coverage: the first line ends with `· coverage partial (1200/3000 files)`, and every block
  whose file was never indexed says `unindexed` instead of a hash.
- Truncated: `[truncated]` appended and `IsTruncated = true`.

### `find_symbol`

```
3 symbols for 'turn' (exact 0, ignore-case 1, partial 2)

1) SharpCoreAgent.Application.AgentLoop.TurnAsync · method · src/SharpCoreAgent.Application/AgentLoop.cs:88-140
   SharpCoreAgent.Application.AgentLoop · callers 2 · callees 7 · community c4
   await foreach (var update in _model.StreamAsync(...))
```
Order is exact match, then case-insensitive match, then substring; within a tier, by path then line
(FR-015). `no symbol named 'xyz' (searched 1 122 declarations)`, success.

### `find_references`

```
2 callers of SharpCoreAgent.Application.AgentLoop.TurnAsync · 2 files · 2 listed

src/SharpCoreAgent.Cli/CliHost.cs
  118-131 · method CliHost.RunAsync · rel calls · sha256:1c40aa9e
     118│var turn = await _loop.TurnAsync(...)
```
Grouped per file, file headings in ordinal order, blocks in line order. `rel` describes the block's
relation to the **subject**: `calls` for a caller, `called_by` for a callee. When `direction=both`, the
heading reads `N references`, and each block's `rel` says which side it is on.

### `code_context`

```
SharpCoreAgent.Application.AgentLoop.TurnAsync · method · src/SharpCoreAgent.Application/AgentLoop.cs:88-140 · community c4

neighbours (hops 1, 8 shown of 26, min weight 0.20)
  calls      SharpCoreAgent.Domain.Model.IModelClient.StreamAsync · src/SharpCoreAgent.Domain/Model/IModelClient.cs:14-20 · w 0.83
  called_by  SharpCoreAgent.Cli.CliHost.RunAsync · src/SharpCoreAgent.Cli/CliHost.cs:118-131 · w 0.61
  implements SharpCoreAgent.Application.IAgentLoop.TurnAsync · src/SharpCoreAgent.Application/IAgentLoop.cs:9-9 · w 0.50

88│public async IAsyncEnumerable<TurnEvent> TurnAsync(
```
Neighbours are sorted by `weight desc, path, line`, so the list is stable. A path target prints the
file's declarations first, then the neighbours of the highest-degree declaration in it.

### `index_status`

```
state fresh · tier lean · embedder hashed-local/512 · quantization int8 · built 2026-10-24T09:12:44Z
files 412 (0 stale, 3 skipped) · passages 1 908 · symbols 1 122 · edges 2 640 · size 14.2 MiB
vector search on · lexical index on · schema 1 · parser 1 · workspace d:/repos/SharpCoreAgent
```
When nothing exists, one line: `state missing · no index at .sharpcoreagent/index; run \`sharpcoreagent index\``.

## Failure modes

| Situation | Result |
|---|---|
| index missing | `Failure`: "the code index is missing for this workspace; run `sharpcoreagent index`" |
| `--no-index` | `Failure`: "the code index is disabled (--no-index); use search_text and read_file instead" |
| another process holds the writer lock | success with a note `index busy (another process is indexing)`; the read still answers from the last complete build |
| schema newer than this build | `Failure`: "the index was written by a newer SharpAriadne; run `index --rebuild`" |
| corrupt file detected on open | `Failure`: "the index is corrupt and will be rebuilt; run `sharpcoreagent index`" |
| `hops` or `k` out of range | `ToolArgumentException` before any query |
| path outside the workspace | `ToolResult.Refused` from `Workspace.TryResolve` |
| no matches / no symbol | success with the "nothing found" line |

## Registration

```csharp
services.AddSharpCoreAgentIndex(options);   // ICodeIndex + ICodeIndexMaintainer (SharpCoreAgent.Indexing)
services.AddSingleton<ITool, SearchCodeTool>();
services.AddSingleton<ITool, FindSymbolTool>();
services.AddSingleton<ITool, FindReferencesTool>();
services.AddSingleton<ITool, CodeContextTool>();
services.AddSingleton<ITool, IndexStatusTool>();
```

The five tools are registered next to the existing six in `AddSharpCoreAgentTools`, so the model sees eleven
tools; the system prompt tells it which question each one answers (see `PromptLibrary`).

