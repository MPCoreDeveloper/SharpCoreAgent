# Plan — 009 MCP client

## Shape

One flag, one host, one connection per server, one adapter per tool.

```text
--mcp "<command>" (repeat) --> CliOptions.McpServers --> SharpCoreAgentOptions.McpServers
                                                              |
                                                              v
                                        ServiceCollectionExtensions.AddMcp (only when named)
                                        |                    |                    |
                                        v                    v                    v
                                 McpToolHost          McpClientOptions     (stderr sink)
                                 (singleton,          (timeouts,           set by the CLI
                                  IDisposable)         ceilings)            under --verbose)
                                        |
                     +------------------+------------------+
                     v                                     v
            McpServerConnection                   McpServerConnection
            (process + JsonRpcPipe)               (process + JsonRpcPipe)
                     |                                     |
                     v                                     v
              McpTool per tools/list entry, registered in the same IToolRegistry
```

Nothing else in the product learns that a subprocess exists: no workspace change, no new path syntax,
no change to the approval model, no change to the loop. An MCP tool is an `ITool`, which is the whole
point of round 001's design.

## The transport

`JsonRpcPipe` is the protocol and knows nothing about processes: it takes an input `Stream` and an
output `Stream`, writes one JSON object per line, and correlates responses by `id`.

```csharp
public sealed class JsonRpcPipe : IAsyncDisposable
{
    public JsonRpcPipe(Stream input, Stream output, Action<string>? malformed = null)
    public Task<JsonElement> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct)
    public Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct)
    public bool IsClosed { get; }
}
```

Three decisions inside it:

- **One reader loop owns stdout.** A single `Task` reads lines, parses each object, and either
  completes a pending request, records a notification (ignored apart from the log), or answers a
  server *request* with a `-32601` error, which is what the specification requires of a client that
  does not implement `sampling/createMessage`.
- **A write gate.** `SemaphoreSlim(1,1)` around the write, because two concurrent tool calls must not
  interleave halves of two JSON lines.
- **Ids from one counter.** A `long` incremented under the same gate; a `ConcurrentDictionary<long,
  TaskCompletionSource<JsonElement>>` holds the waiters. Closing the pipe fails every waiter with the
  reason — that is what turns a dead server into a failed tool call instead of a hung one.

`McpServerConnection` owns the process and the two pipes:

```csharp
public sealed class McpServerConnection : IAsyncDisposable
{
    public string Name { get; }
    public string CommandLine { get; }
    public string ProtocolVersion { get; }
    public Task StartAsync(CancellationToken ct);   // process, handshake, tools/list
    public Task<McpCallResult> CallAsync(string tool, JsonElement arguments, TimeSpan timeout, CancellationToken ct);
    public IReadOnlyList<McpToolInfo> Tools { get; }
}
```

The start sequence is the protocol's, in order: `initialize` (with `protocolVersion: 2025-06-18`,
`capabilities: {}`, `clientInfo: {name: "sharpcoreagent", version: <assembly>}`), read the answer,
send `notifications/initialized`, then `tools/list` until `nextCursor` is absent — at most ten pages,
because a server that pages forever must not hold startup open.

## The process

```csharp
var (fileName, arguments) = McpLauncher.For(commandLine);
// Windows: cmd.exe /d /s /c "<the command line exactly as typed>"
// elsewhere: /bin/sh -c "<the command line exactly as typed>"
var process = new Process { StartInfo = { RedirectStandardInput/Output/Error = true,
                                         UseShellExecute = false, CreateNoWindow = true } };
```

- The command runs through the shell because `npx`, `uvx` and `deno` are shims, not executables.
- `Kill(entireProcessTree: true)` on disposal, plus an `AppDomain.CurrentDomain.ProcessExit` handler as
  the backstop `ShellProcessRegistry` already uses.
- stderr is drained by a second reader loop into a small ring; the ring is echoed through the host's
  log line by line when the CLI sets one (`--verbose`) and is otherwise discarded. It is never parsed,
  never merged with stdout, and never reaches the transcript.
- The child inherits the parent's environment unchanged: a server usually needs its own
  `DATABASE_URL`, and the developer started it on purpose.

## The adapter

`McpTool` is deliberately small: it checks that the arguments are a JSON object, calls the connection,
flattens the content, and reports.

```csharp
public sealed class McpTool : ITool
{
    public McpTool(string name, McpToolInfo info, McpServerConnection connection, int maxChars)
}
```

`content[]` flattening is a switch over `type`:

| Entry | Becomes |
|---|---|
| `text` | its `text` |
| `image` / `audio` | `[image: <mimeType> · <n> base64 characters]` |
| `resource` with `text` | an `[resource: <uri>]` heading, then the text |
| `resource` with `blob` | `[resource: <uri> · <mimeType> · <n> base64 characters]` |
| `resource_link` | `[link: <name> · <uri>]` |
| anything else | `[<type>]` |

Entries are joined with a blank line, and an empty list becomes
`(the <server> server returned no content)`. The joined text is bounded by `TextLimits.Bound` at
64 000 characters before the loop's `MaxToolResultChars` applies; the adapter sets `IsTruncated` when
it cut, so the model is told twice rather than never.

Effects: a destructive call reports `CommandRun` with `<server>: <tool>`, so `/files` and the
observer's tool line say an external process did something; a read-only call reports nothing, because
there is no workspace path to name and inventing one would pollute `/files`.

## Names

```text
<server>_<tool>   folded to [a-z0-9_], runs collapsed, ends trimmed, at most 64 characters
```

`<server>` is the executable stem of the command (`npx`, `uvx`, `mcp-server-postgres`), and a second
server with the same stem gets `_2`, `_3` in the order the flags were given. Within one server, a
duplicate folded tool name gets `_2` as well. A collision against a built-in tool is *not* renamed: it
surfaces as `ToolRegistry`'s duplicate-name `ArgumentException` at construction, which is the loudest
answer available and the correct one — silently shadowing `read_file` would be far worse.

## The wiring

```csharp
services.AddSingleton<McpToolHost>();
services.AddSingleton<IToolRegistry>(provider => new ToolRegistry(
    [.. provider.GetServices<ITool>(), .. provider.GetService<McpToolHost>()?.Started().Tools ?? []]));
```

`McpToolHost` is a singleton because it owns processes that outlive a tool call, and it is
`IDisposable` so the container kills them. `Started()` is idempotent and starts on first use
(synchronously, once, documented) so that a registry resolution cannot silently produce a tool list
missing the servers the developer named. The CLI starts it *before* resolving anything else, so a
broken server is a message and exit 2 rather than an exception three frames deep.

`AddMcp` is called only when `SharpCoreAgentOptions.McpServers` is non-empty, which is the same
structural promise `AddWeb` makes: a process that did not ask for a server has no client, no host and
no `ITool` that could start one.

## The prompt

`PromptLibrary.SystemPromptFor(indexAvailable, webAvailable, mcpAvailable)` gains a third switch and a
`McpRule` that ships only when servers were named — the same reasoning as `IndexRule` and `WebRule`,
with one addition: the rule tells the model that a prefixed tool's answer is data rather than
instruction, because a server is as untrusted as a web page and rather more powerful.

## The command line

| Flag | Parsed into | Refused on |
|---|---|---|
| `--mcp <command>` | `CliOptions.McpServers` (ordered, repeatable) | `models`, `index`, `config`, `setup` |

No settings key (`SettableNames` untouched), no environment variable, and no session field: a resumed
`chat` starts with no servers unless this invocation names them, which is the answer round 7 gave for
attachments and round 2 for the network.

## Complexity Tracking

| Decision | Cost | Why it is worth it |
|---|---|---|
| A protocol implementation in this repository | ~450 lines across five files | a package would be a second trust boundary and a second upgrade cadence for three methods |
| `JsonRpcPipe` over `Stream` instead of over `Process` | one seam | the protocol is testable without spawning anything, which is why the suite stays fast and deterministic |
| A test server project | one console app in `tests/` | the launch path, the stderr drain and the tree kill can only be proved against a real child |
| `Started()` doing sync-over-async | one `.GetAwaiter().GetResult()` | a tool list is a construction-time fact; the alternative is a registry that is right or wrong depending on resolution order |
| Shell launch | a platform branch in `McpLauncher` | `npx` and `uvx` are shims; starting them directly fails on Windows and misleads on Unix |
| Destructive by default | every unannotated tool prompts | the cost of a needless prompt is a keystroke; the cost of a silent write is the file |
| A stderr ring | one buffer per server | stderr is where a Node server writes its own crash, and a developer chasing one needs the last lines |
| Failing the process on a bad server | no partial sessions | a turn with three of the five tools the developer named is the failure mode nobody can debug from a transcript |

## As built

See `tasks.md` for the evidence. Anything that changed while building is recorded here and there.

Four decisions were settled while building rather than before:

- **The Windows launch line is text, not argv.** `McpLauncher.For` returns the file to start, the argv
  form and — on Windows — a raw argument line. Passing the command as one argv element makes .NET escape
  the developer's quotes into `\"C:\…\"`, and `cmd /s /c` then tries to execute a path beginning with a
  backslash-quote. The process test caught it on the first run; the fix is one branch and one remark in
  the launcher.
- **`McpTool` takes `McpClientOptions`, not an `int maxChars`.** The call budget lives in the same record
  as the result ceiling, and passing two numbers from one object invites them to disagree.
- **`McpToolInfo` and `McpCallResult` are public.** `McpServerConnection` is a public type whose `Tools`
  and `CallAsync` are part of what it is, so both had to be as visible as the members that expose them.
- **`McpToolName.WithSuffix` trims the head, not the suffix.** Capping an already-maximum-length name
  would drop the `_2` and leave the collision the suffix exists to resolve.



