# Implementation Plan: Evidence over MCP (`mcp serve`)

**Branch**: `013-evidence-mcp-server`

**Spec**: [spec.md](spec.md)

**Date**: 2026-10-28

**Status**: Accepted 2026-10-28 — constitution 1.2.0 and the round-6 roadmap row are agreed, so the six phases
below are the plan of record. `tasks.md` carries the task boundaries.

## Summary

Publish the six read-only evidence tools over stdio MCP so an agent that is not SharpAgent — Copilot agent
mode, Claude Code, Codex CLI — can ask this repository's index where something is. No new retrieval
behaviour, no new tool, no model, no network, no write path: one command, one protocol loop, one filtered
tool set, one measurement.

The engine is already there (`SharpCoreAgent.Indexing`, SharpCoreDb, lexical + vector + graph). The tools
are already there (`SearchCodeTool` and four siblings). What is missing is a process that answers
`initialize`. That is what this plan adds, and the discipline is to add nothing else.

## Where the code goes

| Layer | Add | Why here |
|---|---|---|
| `SharpCoreAgent.Infrastructure` | `McpServer/McpServerOptions.cs`, `McpServerPublishedSet.cs`, `McpServerSession.cs`, `StdioRpcWriter.cs` | An adapter over `ITool`/`IToolRegistry` and the index. Infrastructure already owns `Mcp/`; the client and the server are two adapters over the same wire format, and the layering rule (Infrastructure → Domain + Application) is unchanged. |
| `SharpCoreAgent.DependencyInjection` | `AddMcpServer(this IServiceCollection, Workspace, McpServerOptions)` | Same shape as `AddWeb`: register the adapter's tools and nothing else. |
| `SharpCoreAgent.Cli` | `Commands/McpCommand.cs`, `Commands/McpBench.cs`; `McpAction { Serve, Config, Bench }` in `CliOptions`; rows in `HelpText` | `mcp` is a command with an action, exactly like `config` with `ConfigAction`. |
| `tests/SharpCoreAgent.Tests` | `Infrastructure/McpServer*Tests.cs`, `Infrastructure/McpServerDogfoodTests.cs`, `Cli/McpServeParsingTests.cs`, `Cli/McpConfigParsingTests.cs`, `Cli/McpBenchTests.cs` | Named for the surface they prove; the server's own tests live beside the client's because they share the wire fixtures, and the CLI-facing ones stay under `Cli/`. |

Nothing in `Domain` or `Application` changes. `PublicAPI.Unshipped.txt` is untouched: only `Domain` carries a
public-API file, and the new types are `internal` except `McpServerSession`/`McpServerOptions`, which live in
a project that has no such file.

## The protocol loop

- **Framing**: one JSON object per line on stdin and stdout, UTF-8 without BOM. This is the format our own
  client speaks (`JsonRpcPipe`) and the format `tests/SharpCoreAgent.TestMcpServer` already proves.
- **Ownership**: one reader loop, one writer guarded by a semaphore, one dictionary of in-flight calls keyed
  by JSON-RPC id so `notifications/cancelled` can find its target. The mirror image of `JsonRpcPipe`'s three
  invariants, and for the same reasons.
- **Ordering**: one request at a time, in arrival order (FR-015). A file-backed index with a single writer
  lock is not a throughput problem to solve; determinism is worth more, and a test can then assert order.
- **Purity**: `Console.Out` is never written. The loop writes to a private writer over stdout that refuses a
  string containing a newline (a marshalling bug becomes an exception in the test, not a corrupted frame),
  and every diagnostic goes to `Console.Error` behind `--verbose` except the one ready line.

### Why not reuse `JsonRpcPipe`

`JsonRpcPipe` initiates requests and keeps a waiter table; a server answers them. Generalising it would put
the shipped, tested client path (`009`, merged as `be0b265`) at risk for the benefit of sharing about forty
lines of envelope construction. The plan copies those forty lines into the server, and FR-017's dogfood test
makes the two halves prove each other: if either drifts, our own client stops understanding our own server.

### Errors

| Situation | Answer |
|---|---|
| body is not JSON | `-32700`, `id: null` |
| no `method`, or `method` not a string | `-32600`, id when there is one |
| unknown method | `-32601` |
| `tools/call` with a name outside the published set | `-32602`, message names the tool |
| `tools/call` with `params` that are not an object | `-32602` |
| `tools/call` whose `arguments` is absent | treated as `{}`, so the tool's own validation answers |
| a tool that returns `IsError` | **not** a JSON-RPC error: `result.isError = true` with the tool's text |

The last row is the one that matters. A tool-level failure is a sentence the model should read and act on;
a JSON-RPC error is a protocol fault.

## The published set

`PublishedSet = allowList ∩ { tools whose Safety is ReadOnly }`. Both halves are required: the allow-list
means a future read-only tool cannot join by accident, and the safety filter means a name on the list that
somehow became destructive cannot be served.

| Name | Why an outside agent needs it |
|---|---|
| `search_code` | the round's headline: ranked passages with `path:start-end`, a hash and the legs that found them |
| `find_symbol` | "where is `ToolResultTrimmer` declared, and is it a class or a record" |
| `find_references` | "who calls this before I change its signature" |
| `code_context` | one hop of the symbol graph around a file or declaration |
| `index_status` | whether the other four can be trusted, and what to run when they cannot |

`write_file`, `delete_file`, `replace_in_file`, `run_shell`, `shell_output`, `shell_stop`,
`list_directory`, `read_file`, `search_text`, `list_roots`, `fetch_url` and any `McpTool` are **not**
published. No flag changes that (FR-010): a `--read-tools` flag is the obvious next request and it is
refused here on purpose — an editor agent already reads files, and the index is what it lacks.

## Index lifetime, the writer lock, and refresh

Two questions the round would rather answer in the plan than discover in the field.

**Does a running server block `sharpcoreagent index`?** It must not: a developer whose editor holds a server
open still expects to run a build in a terminal. SharpCoreDb is a single-file database behind a
process-wide handle cache (`IndexHandleCache`), and the single-writer rule is a file handle on `.lock`
(`IndexWriterLock`, held only for the length of a build). So the server leases the handle for the length of
one call and releases it afterwards (FR-014), instead of holding an open database for an eight-hour editing
session. The cost is one file open per call, which SC-005's 300 ms budget is there to keep honest.

**Who keeps the index current?** The server does, within the same budget the agent loop uses: before a
`tools/call` answers, `EnsureFreshAsync(3 s)` runs, at most once every 5 s (FR-012). No lock probing happens
in the server — `EnsureFreshAsync` already answers "another writer holds it" as a report rather than an
exception, so the lock question stays inside the component that owns it. `--no-refresh` exists for a shared
or read-only checkout, and `initialize`/`tools/list` never refresh, so a slow first refresh cannot look like
a slow handshake.

If the refresh cannot run (no index, busy, corrupt, budget spent), the answer is still produced and the
heading the tool already writes says what state the index is in. Failures of freshness are information, not
errors (FR-013).

## CLI surface

```
sharpcoreagent mcp serve [--workspace <path>] [--no-refresh] [--verbose]
sharpcoreagent mcp config --client <claude|vscode|codex>
```

- `mcp` is a new `CliCommand` with a `CliAction`, the shape `config` already uses. `--mcp <command>` (the
  client, round 3) is unchanged and cannot collide: one is a flag, the other a command.
- `mcp serve` refuses `--web`, `--web-allow`, `--mcp`, `--provider`, `--model`, `--base-url`, `--fallback`
  and `--session` with a usage error (exit 2). It cannot use them, and a silently ignored capability is how
  a developer ends up believing the server fetches pages.
- `--workspace` is reused as-is; `--no-refresh` is the only new flag.
- Exit codes: 0 when stdin closes or Ctrl+C cancels a call; 2 for a usage error before the handshake; 1 for an
  unexpected failure (with `--verbose` for the stack trace). No new exit code is introduced.
- `mcp config` prints to stdout and exits 0; it reads no settings and needs no workspace.

## Composition root

`AddMcpServer(workspace, options)` registers, in order: `SharpCoreAgentIndex.Open(workspace)` as itself,
`ICodeIndex` and `ICodeIndexMaintainer` (the same three lines `AddSharpCoreAgentIndex` writes today), then
the six tools — five index tools plus `ViewImageTool` — then `McpServerSession`.

It registers **no** `IChatModel`, no `ICredentialStore`, no `IChatModelResolver` and no `HttpClient`. That is
FR-001 and it is testable: a test resolves the session from a container with no key in the environment and
asserts it answers `tools/list`. The point is not purity for its own sake; it is that an agent starts this
process dozens of times a day on a machine where the developer may never have typed a key for SharpAgent.

## Tests

| Test | Proves |
|---|---|
| `McpServerSessionTests` | FR-004–FR-008: handshake, list, call, ping, notification silence, cancellation, every error code, and that a malformed line before a good one does not end the loop |
| `McpServerPublishedSetTests` | FR-009–FR-011 and SC-003: set equals the allow-list, every member is `ReadOnly`, `write_file` and friends are absent, `tools/call` on them is `-32602` |
| `McpServerStdoutTests` | FR-003 and SC-004: each stdout line parses as one object; diagnostics appear only on stderr |
| `IndexRefreshGateTests`, `LeasedCodeIndexTests` | FR-012/FR-013 and FR-015: stale count drops after a refresh; a second writer's build yields an answer plus `building`; budget exhaustion is not an error; a lease leaves no lock behind |
| `McpServerDogfoodTests` | FR-017: our own `--mcp` client against our own server on a fixture workspace, tool names `sharpcoreagent_<tool>`, evidence text identical to the in-process call (SC-002) |
| `McpServeParsingTests`, `McpConfigParsingTests`, `McpConfigCommandTests` | FR-001/FR-002: `mcp serve` accepts `--no-refresh`, refuses the eight flags above, `mcp config --client` requires a known client, `mcp bench` takes no operand and no flag of another verb |
| `McpBenchTests` | FR-019: the question file, the ratio's two halves, the percentile, and one end-to-end run over a real server (SC-005, SC-006) |

`dotnet test` at the close of the round, in Release: **1085 passing, 0 failed, 0 skipped**.

Every test runs from streams or a child process with no live endpoint and no key, which is the existing rule.

## Measurement (SC-005, SC-006)

The claim this round is allowed to make is the one it measures. `specs/013-evidence-mcp-server/bench/queries.txt`
holds twelve questions a developer would actually ask of this repository ("where is a tool result trimmed to
the budget", …). `sharpcoreagent mcp bench` reads that file — there is no flag, so the questions a published
number was measured on cannot be edited at the keyboard — and for each query:

1. calls `search_code` in-process and over stdio, asserting both texts are identical (SC-002);
2. sums the characters of the distinct files the answer cites, and the characters of the answer;
3. reports per query and in total: characters spent, characters of the sources, the ratio, and the wall-clock
   of the stdio call (SC-005 p95).

The harness is `sharpcoreagent mcp bench` rather than a PowerShell script, for the same reason the
index command exists: it runs wherever the product runs, it shares the tool code path, and it is testable. It
requires a built index and no key, and its numbers are pasted below before the merge. A number that does not
beat "read the files" is the most important result this round could produce, and it is published the same way.

The plan asked for `[--json]`. It was dropped in implementation and the reason is recorded rather than the
flag: the table is the artefact the numbers are pasted from, a second shape would be a second thing to keep in
step, and the option table has no column wide enough to name a fourth command for that flag. `--verbose` was
added in its place, and it is what the analysis below is built on.

### What it printed (2026-10-29, this repository)

606 files, 12 420 passages, 233.4 MiB index, `state fresh`, Windows 11, Debug build, nothing else running:

```
bench · 12 queries · evidence 138 723 chars · sources 8 134 414 chars · ratio 0.017 · p95 4425 ms · parity 12/12
```

| answer chars | cited source chars | ratio | stdio ms | query |
|---|---|---|---|---|
| 8 283 | 438 575 | 0.019 | 4 425 | where is a tool result trimmed to the character budget |
| 14 529 | 121 683 | 0.119 | 3 204 | which wrapper opens the index for one call and releases it again |
| 13 094 | 83 331 | 0.157 | 3 334 | where is an MCP tool name folded into letters and underscores |
| 10 616 | 338 661 | 0.031 | 2 892 | how is the refresh budget kept between two calls |
| 11 766 | 1 206 775 | 0.010 | 2 671 | which type writes one JSON-RPC frame on a single line |
| 8 848 | 617 678 | 0.014 | 2 825 | where is the exit code chosen for a refused flag |
| 11 605 | 967 760 | 0.012 | 2 950 | how does the agent know which tools may run without asking |
| 9 745 | 968 354 | 0.010 | 2 946 | which file writes the transcript of a turn |
| 10 691 | 970 551 | 0.011 | 2 971 | where is a provider's rate limit read from its answer |
| 12 898 | 778 667 | 0.017 | 2 695 | how is the line range of an evidence block addressed |
| 14 144 | 643 013 | 0.022 | 2 801 | which command prints the settings file in use |
| 12 504 | 999 366 | 0.013 | 2 832 | where is the search answer rendered with its heading |

**SC-006: met, and not by a small margin.** 138 723 characters of evidence stood in for 8 134 414 characters of
source — a ratio of **0.017**, or about 1/59th. The worst query is still 1/6th (0.157) and the best is 1/100th.

**SC-005: missed, by an order of magnitude.** The p95 of the stdio call is **4.4 s** against a budget of 300 ms.
`--verbose` prints both halves per query and they are the same number — in-process 2.6–4.6 s, stdio 2.7–4.4 s —
so the hop costs nothing measurable: what costs three seconds is the *call*, on both sides. That is the lease
Complexity Tracking item 3 said would be measured rather than assumed: one open of a 233 MiB index per call,
plus the graph hop over 12 420 passages, is what a client waits for. The honest reading is that this is
affordable for an agent's first move in a turn and not for a loop that asks ten questions, and the number is
published rather than tuned away by quoting the answer instead of the call.

**A second result that is not good news: the ratio only means something at scale.** The same command on a
two-file fixture prints `ratio 3.427` — 353 characters of answer against 103 characters of source — because the
heading, the hash and the line numbers alone cite more than the file holds. SC-006 is a claim about a real
repository, and a small workspace would have produced a number that flattered nobody.

**Observation, not this round's bug:** the incremental `index` run before the measurement crashed once with
`error: Index was outside the bounds of the array.` after the tree had grown a few hundred files; `index
--rebuild` (841 s) repaired it. Recorded here because a bench over a corrupt index would have published twelve
identical failures as a result. It belongs to round 002 and is filed there as **T035** rather than fixed in
passing — and it was worse than a crash: the engine's `AUTO` counter restarted at one on every new connection,
so each build after the first stored its files under ids an earlier build already held. T035 names the cause
and fixes it (the writer allocates every id itself, `schema_version` 2).

**The numbers above survive T035, and it is worth saying why.** They were taken *after* `index --rebuild`, and a
rebuild empties every table and re-derives the corpus in one session, so the rows it wrote carry ids 1…N once
each. The duplicate ids T035 describes belong to the incremental runs before it and were cleared by exactly that
rebuild. A bench taken without the rebuild would have measured a file with repeated rows and counted passages
twice.

### What it printed (2026-11-02, this repository, after round 16)

Round 16 (`025-served-index-snapshot`) did the work this plan left open — one snapshot across per-call handles, and a
harness that says what `warm` means — and the same command on the same fixture now prints:

```
bench · 12 queries · evidence 143 825 chars · sources 6 452 613 chars · ratio 0.022 · p95 261 ms · cold 2641 ms · parity 12/12
bench · 12 queries · evidence 143 825 chars · sources 6 452 613 chars · ratio 0.022 · p95 248 ms · cold 2834 ms · parity 12/12
```

**SC-005: met.** 261 ms and 248 ms against a 300 ms budget, down from 3 199 ms. Two things had to move out of the
call, and the round found the second one by measuring rather than by reasoning: the snapshot (3 917 ms cold, 0 ms
warm) and the status every tool reads before it answers (1 347–1 435 ms on a fresh instance, 0 ms when remembered).
Both are now held per database rather than per instance, keyed by the build id and the file stamp they already used,
so a rebuild in this process or any other is still seen by the next call. The handle is still opened and released
per call — `LeasedCodeIndex` was not touched, and its test that proves a build in another terminal is never waiting
on an idle editor passes without an edit.

**Cold is 2 641–2 834 ms and is printed beside the p95.** The first call after a cold start is the one that loads
the build and computes the status; with twelve samples the nearest-rank p95 *is* the slowest sample, so the harness
now spends one untimed call before the samples and reports that call separately. The criterion is about
"a warm `search_code`", which is what it now measures.

**SC-006 stands**: ratio 0.022 on the grown index, against 0.017 at 606 files — the same order, measured on a tree
that has since doubled.

## Complexity Tracking

Four places this plan chose more work than the minimum. Each is deliberate.

1. **Copying the envelope code instead of generalising `JsonRpcPipe`.** ~40 duplicated lines, kept because the
   alternative edits a merged, tested client path whose failure mode (a hung turn) is worse than a duplicated
   constant. The dogfood test is the guard against drift, and it costs one process launch.
2. **Two gates on the published set (allow-list ∩ ReadOnly).** Redundant today by construction. Kept because
   the redundancy is the requirement: FR-009 must fail loudly when someone adds a read-only tool, not quietly
   widen the server's surface by a `Safety` property that happened to be true.
3. **A lease per call rather than one long-lived handle.** One file open per `tools/call` buys the guarantee
   that an editor's server does not stop a terminal build. The cost is measured, not assumed.
4. **Refresh on by default.** A server that answers from a stale index is a server whose answers get ignored
   after the first wrong line number. Five seconds of gating and a three-second budget is the price; the
   alternative is asking a developer to remember to run `index` before every prompt.

## Risks and open questions

| Risk | Handling |
|---|---|
| SharpCoreDb may hold the single-file database with an OS-level exclusive handle even for reads | Verify during phase 3 by building from a second process while a server holds a lease; if it does not work, the fallback is a documented "stop the server to rebuild" plus a `tools/call index_status` hint, and the quickstart records it |
| The three agents' configuration file shapes drift (VS Code `.vscode/mcp.json`, Claude Code `.mcp.json`, Codex `config.toml`) | Snippets are verified against each agent's current documentation in phase 5, cited in the README, and the doc says which version was checked |
| A cold index makes the first `search_code` slow enough that an agent gives up | `initialize`/`tools/list` never refresh; the first `tools/call` gets the 3 s budget and answers either way; the README tells the developer to run `index` once after cloning |
| Windows console encoding leaks a BOM or a code page glyph onto stdout | The writer is `UTF8Encoding(false)` and line-asserting (FR-003); the existing `UseUtf8Output` already skips a redirected stream |
| A second consumer exposes a bug our own client never hit (different argument shapes, missing `arguments`) | `McpServerSessionTests` covers absent/odd arguments; the dogfood test covers the happy path end to end |

## Governance and reach

- **Constitution**: this adds a fourth surface (`mcp serve`) to a document whose 1.1.0 migration note names
  three (`--add-root`, `--web`, `--mcp`). It is a MINOR amendment — 1.1.0 → 1.2.0 — stating that the index may
  be *served* to a process the developer names, that only read-only evidence tools are publishable, and that no
  key, no network and no write path are involved. Every default stays as it is: a build that configures
  nothing still answers nothing.
- **Roadmap**: round 6, relying on `002` (landed) and nothing else. It reverses one sentence in "What stays out
  of this program" — MCP *server* mode — and the reversal is recorded with the reason it was wrong then and
  right now. It does not touch rounds 4 and 5 and it does not answer `009` T034, T035 or T037; `009` T036 keeps
  the half of server mode this round does not take (reconnection and supervision).
- **Not in this round, and said out loud**: resources, prompts, sampling, HTTP transports, `--read-tools`,
  multiple workspaces, a second tool catalogue.

## Phases

| Phase | Work | Closes with |
|---|---|---|
| 1 | Envelope, parser, writer, session skeleton, `initialize`/`ping`/errors | `dotnet build` + session tests green |
| 2 | `tools/list`, `tools/call`, cancellation, published-set gate | published-set and stdout tests green |
| 3 | `AddMcpServer`, lease-per-call, refresh gate, `mcp serve` wiring, exit codes | refresh tests, cross-process lock check |
| 4 | `mcp config --client`, `HelpText`, `contracts/mcp-server.md`, `cli.md` row | parsing tests, help golden test |
| 5 | Dogfood test, `mcp bench`, the twelve queries, the numbers in this file | full suite, numbers recorded |
| 6 | Constitution 1.2.0, roadmap row, README section, suite count, merge `--no-ff` | the round's definition of done |

`tasks.md` was written when this plan was accepted, because its task boundaries are exactly these phases and
writing it before the reversal was approved would have been a plan for a round that might not run.
