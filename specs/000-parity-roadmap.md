# Roadmap — the capability rounds

**Status**: round 0 landed (constitution 1.1.0, amended to 1.2.0 for round 6), round 1 landed on `main` (merge
`dbc0701`), round 2 landed on `main` (merge `201a431`), round 3 landed on `main` (merge `be0b265`), round 6 landed
on `main` 2026-10-29 in eight commits (`aabf2ae`…`a9d4b13`) with no merge commit — it landed phase by phase instead
of on a branch, and its `tasks.md` records that rather than inventing one — round 7 (`015`) landed complete on
2026-10-30, whole suite 1167/1167 with zero diff on the index, and round 5 (`011`) landed complete the same day,
whole suite 1190/1190 and no new line in `Domain`, `CodingAgent`, `Domain/Index` or `SharpCoreAgent.Indexing` —
round 4 (`010`) landed complete the same day, whole suite 1200/1200 at 0 warnings, with no hunk of this round on
the four paths it may not touch. Those three rounds and `012` are one commit, `a447a4d`, because their diffs
cannot be separated: `README.md`, `HelpText.cs`, `CodingAgent.cs` and this roadmap each carry hunks from more
than one of them, and any split would leave an intermediate commit whose registration tests fail. Rounds 8 and 9
landed on `main` on 2026-10-31 as `6ac0c90` and `e69cbdc`, with the release workflows behind them as `278eb74`:
three commits, each of which builds. `PromptLibrary.cs` is the one source file both rounds edit, so the first
commit carries its round-8 shape — its `ReviewInstruction`, and none of round 9's operating rule 9 or
`ImageAttached` — and the second restores the rest; every intermediate tree was built and tested before it was
committed rather than after, 1228/1228 for round 8 and 1256/1256 for round 9, both at 0 warnings.

**Supersedes**: nothing. `001`–`006` stay the record of what was built and why.

## Why rounds rather than one specification

`001`–`006` each shipped one capability behind one reviewable change. This program adds what a
developer arriving from Cline, Codex CLI or Claude CLI reaches for on the first day, and each piece
is independently shippable, independently testable and independently reviewable. As one branch it
would be a month of work nobody can hold in their head; as a round each it is one merge each, and any one of
them can be reverted on its own. The constitution was amended once, at the front (1.0.2 → 1.1.0), so no round has
to negotiate permission for its own existence — except round 6, which came later and needs its own amendment,
and says so where it is proposed.

## What "parity" means here

The four gaps a newcomer hits, in the order they hit them.

1. *"That file is in another repository."* — a turn can see exactly one tree; attaching a second one
   is impossible without moving the first.
2. *"Read this page and tell me what it says."* — there is no way to fetch a URL at all.
3. *"Use the browser, GitHub, Postgres."* — there is no extension mechanism, so every integration is a
   new project in this repository.
4. *"Do this in a sub-agent, keep my context clean."* — `task` was drafted
   (`specs/011-subagent-task/TaskTool.cs.pending`) and never finished; round 5 finished it on 2026-10-30.

## The rounds

| Round | Spec | Ships | Depends on |
|---|---|---|---|
| 0 | `.specify/memory/constitution.md` **1.1.0** | permission for rounds 1–4, and nothing else | — |
| 1 | [`007-external-roots`](007-external-roots/spec.md) | read another project; never write it | 0 |
| 2 | [`008-web-fetch`](008-web-fetch/spec.md) | `fetch_url` — a page as bounded, readable text (landed as `201a431`) | 0 |
| 3 | [`009-mcp-client`](009-mcp-client/spec.md) | `--mcp <command>` — any stdio MCP server's tools (landed as `be0b265`) | 0 |
| 4 | [`010-browser`](010-browser/spec.md) | a browser you name, and the screenshots it returns (landed 2026-10-30 as `a447a4d`: `McpArtefactSink`, the `Flatten` save delegate, two ceilings, 10 tests) | 3 |
| 5 | [`011-subagent-task`](011-subagent-task/spec.md) | `task` — delegation with an isolated transcript (landed 2026-10-30 as `a447a4d`: `TaskTool`, `TaskFormat`, `NoPromptApprover`, the registration, prompt rule 8, 23 tests) | — |
| 6 | [`013-evidence-mcp-server`](013-evidence-mcp-server/spec.md) | `mcp serve` — the index's evidence for Copilot, Codex and Claude Code, over stdio | 0 (amended to 1.2.0), and `002` (landed) |
| 7 | [`015-fsharp-peer`](015-fsharp-peer/spec.md) | `file_diagnostics`, `uses_at`, `signature_at` — a compiler's view of a file, behind `--fsharp` (landed 2026-10-30 as `a447a4d`) | 0, and `012` (landed) |
| 8 | [`016-model-roles`](016-model-roles/spec.md) | `--planner` / `--executor` / `--reviewer`, `/roles`, `/review`, and a pasted key instead of a failed first run (landed 2026-10-31 as `6ac0c90`: `AgentRoles`, `CredentialPrompt`, the upsert in `CredentialFile`, `AgentSession.SessionModel`, 28 tests in 4 new classes) | 0, `004`, `005`, `006` (all landed) |
| 9 | [`017-image-view`](017-image-view/spec.md) | `view_image` — a picture the model can actually see, and the `ImageContent` on `Domain.Conversation` that carries it (landed 2026-10-31 as `e69cbdc`: `ImageContent`, `ChatMessage.Images`, `ToolResult.Images`, the eighteenth default tool, prompt rule 9, 1256 passing; closes `010` T035) | 0, and `010` (landed) |

`web_search` is deliberately unnumbered: it needs a provider and a key, which is a decision this
repository has not made. If it is made, it becomes `018` and it does not change 007–011. (`012` went to
the compiler oracle — [`012-compiler-oracle`](012-compiler-oracle/spec.md) — which needs neither a provider
nor a key, `013` is the evidence server, `015` is F# code intelligence —
[`015-fsharp-peer`](015-fsharp-peer/spec.md) — which links the F# front end in rather than talking to a
server, `016` is model roles and `017` is the image view.)

## Decision record

- **The browser is not a binary we ship.** A native headless browser would break the "no native
  binaries, no platform-specific code" constraint and add a 100–300 MB download to a tool that
  currently ships as assemblies. Round 3 gives the extension seam; round 4 uses it with the Playwright
  or Puppeteer MCP server the developer already has, which is exactly how Cline, Codex CLI and Claude
  Code get browser control. If in-box automation is ever wanted, it is a new amendment and a new
  round, not a change to round 4.
- **Round 4 does not add a `browser` tool, and does not add a `--browser` flag.** Decided 2026-10-28,
  with round 3 landed, because `--mcp` already publishes a server's tools under its own names —
  `playwright_browser_navigate` is the browser tool, complete with the schema its authors maintain. A
  `browser` tool of ours would shadow that schema with a second and worse protocol client, and a
  `--browser` flag would bake a third-party command line and its version into a flag, which is the
  decision the developer is supposed to make when they type `--mcp`. What round 4 has to add is the one
  thing `--mcp` cannot: `McpContent.Flatten` describes an `image` or a `blob` as a MIME type and a size,
  so a screenshot is currently a sentence. It would reverse if server authors converge on a rendering our
  flattening cannot express, or if a first-party browser server appears that we would pin and ship.
- **The network stays off until asked for.** `--web` and `--mcp` are per-process opt-ins. A build that
  configures nothing makes the same requests it makes today.
- **Attached roots are read-only and per invocation.** Not persisted, not writable, not indexed.
  Rationale in `007-external-roots/spec.md`; the short version is that a directory the agent may read
  is context for one task, and a persisted list is authority nobody remembers granting.
- **Delegation is not a new process.** `task` runs the same orchestrator in-process with an isolated
  transcript, which keeps one budget, one approval mode and one transcript format.
- **MCP *server* mode is back on the table, and the earlier exclusion was right when it was written.**
  Proposed 2026-10-28, after round 3 landed. `002` drew the line — "out of scope: an MCP server (the index is
  in-process)" — when the only consumer of the index was our own loop and no client existed in this product;
  `009` then made us a *client*, which is the direction that needed building first. What that round taught is
  where the mistake is: `--mcp "npx -y @playwright/mcp@latest"` is how we get a browser, but nothing on the
  developer's machine can call *our* index, so Copilot, Codex CLI and Claude Code still start every task blind
  — the exact cost `002` was built to remove. Round 6 (`013-evidence-mcp-server`) publishes the five read-only
  evidence tools over stdio and adds nothing else: no write path, no shell, no network, no key, no HTTP
  transport, no second tool catalogue. It reverses the exclusion, not the reasons behind it: the index is
  still in-process, and what leaves the process is evidence, which is the one thing `002` already argued is
  safe to hand over. It would reverse *back* if a served tool ever needed a write, or if a transport beyond
  stdio became a requirement — either is a new amendment and a new round.

## Per-round deliverables (the definition of "done")

Every round, without exception:

1. `specs/<###>-<name>/spec.md`, `plan.md` (with **Complexity Tracking**), `tasks.md`, and
   `contracts/*.md` for any CLI, wire or tool surface the round changes.
2. Code and the tests that fail if the behaviour is removed (principle VI). No test calls a live
   endpoint and no test needs an API key.
3. New public types listed in `PublicAPI.Unshipped.txt` in the same commit (`RS0016`/`RS0017`).
4. `HelpText`, `README.md` and `specs/001-coding-agent-core/contracts/cli.md` updated where a flag, an
   exit code or a tool changed — `docs/` and `README.md` are governed as code.
5. `dotnet build .\SharpCoreAgent.slnx -c Release` with zero warnings and `dotnet test` green, before
   the merge and not after it.
6. One branch off `main`, one merge with `--no-ff`, the branch deleted locally and on the remote, and
   the suite count in `README.md` corrected to the number the run printed — or, for a round that lands
   as commits straight on `main`, those commits, with the suite count corrected the same way. Rounds 6,
   8 and 9 took the second route: no branch and no merge commit, so the commit hash is the record.
7. The round's `spec.md` metadata switched from `specified` to `implemented` or `landed`, naming the
   branch it actually merged from or, when there was no branch, the commit it landed as.

## Round 0 — the amendment (landed in this change)

Three constraints materially expanded, one clarified, no principle removed:

- **II** — a workspace may carry additional roots the developer named explicitly; readable, never
  writable; writes and shell commands still resolve against the primary root alone.
- **III** — `Process.Start` gains a second exception shaped like the first: the MCP client, a process
  the developer named, never one the model chose.
- **VIII** — "and nowhere else" is narrowed to no telemetry, phone-home or update check; network
  beyond the model endpoint is an opt-in, tool-mediated capability.
- **Technical Constraints** — an MCP server the developer supplies is not a shipped native binary.

Deliverable: the amended `.specify/memory/constitution.md` with its Sync Impact Report and migration
note. No code, and every default unchanged.

## Round 1 — `007-external-roots` (landed as `dbc0701`)

**Ships**: `--add-root <path>` (repeatable) and `SHARPCOREAGENT_ADD_ROOTS`; a read path through
`Workspace`/`PathSafety` that accepts an absolute path inside any attached root; `read_file`,
`list_directory` and `search_text` honouring it; every write, delete and shell call still confined to
the primary root; a `list_roots` tool; a banner line and `/roots`.

**Does not ship**: writes into an attachment, a second index, persisted attachments, per-root
approval.

**Evidence**: a turn reads a file in a second project and cannot write it; a link inside an attachment
that points outside every root is refused; no existing assertion about write confinement is deleted to
make the round pass.

**As built**: everything above, with four recorded departures from the plan in
`007-external-roots/plan.md` — `search_text` grew the `path` argument it needed to be aimed at an
attachment, `owner` is `null` for the primary root, the banner shortens an attachment path to keep the
count in view, and a resumed `chat` re-applies the current invocation's roots. `tasks.md` keeps T030
(writable attachment), T031 (indexed attachment) and T032 (persisted roots) open as the record of what
the round refused.

## Round 2 — `008-web-fetch` (landed as `201a431`)

**Ships**: one read-only tool, `fetch_url`, behind `--web`. It fetches `http`/`https` only, with a
timeout, a byte ceiling, a redirect ceiling and a character ceiling; it refuses loopback, link-local,
private and cloud-metadata addresses unless the developer names them; it sends no credential and no
cookie. For HTML it returns a readable reduction — entities decoded, `script`/`style` removed, tags
stripped, whitespace collapsed — implemented in the BCL, not with a new dependency. The result names
the final URL after redirects, the status, the content type, and whether the text was truncated.

**Does not ship**: JavaScript execution, cookies, logins, screenshots, a search engine.

**Evidence**: a deterministic `HttpMessageHandler` stub replaying recorded responses, in the shape the
provider adapters are already tested with — no live network in the suite.

**As built**: as above, with three details a reader of the plan would otherwise have to rediscover. The
redirect ceiling is the tool's own loop rather than the platform's, because automatic redirects would
skip the address check on every hop after the first — so `AllowAutoRedirect = false` is part of the
contract, not a preference. `--web-allow <host>` without `--web` is a usage error rather than an
accepted no-op. And the shipped prompt's path rule was corrected in the same change: it claimed every
path outside the workspace root is rejected, which round 1 made untrue for an attachment.
`tasks.md` keeps T029 and T031–T033 open as the record of what the round refused — JavaScript and
cookies, page paging, a search tool and `robots.txt` — and T030 closed on 2026-10-28, after the merge, by
`WebFetchHandler`: the address is judged a second time inside the connect callback, and `UseProxy` is
`false`, because a proxy would answer the question the address rule exists to answer.

## Round 3 — `009-mcp-client` (landed as `be0b265`)

**Ships**: `--mcp <command>` (repeatable), a stdio JSON-RPC client (`initialize`, `tools/list`,
`tools/call`, notifications), and an adapter that turns each MCP tool into an `ITool` in the same
`IToolRegistry` the built-in tools use. `readOnlyHint` maps to `ToolSafety.ReadOnly`; anything else is
`Destructive` and prompts. Results are bounded by `MaxToolResultChars`; the server's stderr goes to
`--verbose` and never into the transcript. The child is killed when the session ends, the way
`ShellProcessRegistry` kills a shell command.

This closes `T040` in `specs/003-terminal-console-agent/tasks.md`, which has been open since 003.

**As built.** `--mcp "<command>"` is repeatable and per-invocation; the command runs through the
platform shell (`cmd /d /s /c` on Windows with the line built by hand, `/bin/sh -c` elsewhere with one
argv element), because `npx`, `uvx` and `deno` are shims — the first process test found that passing a
pre-quoted command as an argv element makes `cmd` try to execute `\"C:\…\"`, so the Windows line is
built as text and the Unix one as argv. The protocol is hand-written in five files under
`src/SharpCoreAgent.Infrastructure/Mcp/`: `JsonRpcPipe` owns one reader loop, one write gate and one id
counter, answers a server request with `-32601`, and fails every waiter when the pipe closes;
`McpServerConnection` owns the child, the handshake and pagination; `McpToolHost` owns one connection per
flag and the idempotent synchronous `Started()` the registry factory uses. Names are `<server>_<tool>`
folded to `[a-z0-9_]` and capped at 64 characters, with `_2` for a repeated stem and `_2` again for a
repeated tool name inside one server. `AddMcp` runs only when a server was named, so a process that asked
for none has no client, no host and no tool that could start one; the prompt gains `McpRule` under the
same condition. Budgets: 20 s control, 120 s call, 64 000 characters per result, 10 list pages, 200
stderr lines. The evidence is `JsonRpcPipeTests`, `McpProtocolTests`, `McpToolNameTests`, `McpToolTests`,
`McpOptionTests`, `McpRegistrationTests`, `McpLauncherTests` and `McpProcessTests` — the last against a
real server in `tests/SharpCoreAgent.TestMcpServer`, which proves the launch path, the stderr split and
the tree kill — and the suite is 939 tests, all green with no compiler warnings. `tasks.md` keeps
T034–T037 open as the record of what the round refused: resources, prompts, sampling, remote transports,
server mode and a declarative server list.

## Round 4 — `010-browser` (landed 2026-10-30 as `a447a4d`)

**Status**: landed, both phases. `McpArtefactSink` sits in `src/SharpCoreAgent.Infrastructure/Mcp/` beside
`McpContent`, which gained the `save` delegate and a two-argument overload that passes `null`; `McpTool` builds
one sink per call and reports a `Created` effect per file after any `CommandRun`; `McpClientOptions` carries
`MaxArtefactBytes` (8 MiB) and `MaxArtefactsPerCall` (8). The contract is
[`010-browser/contracts/mcp-content.md`](010-browser/contracts/mcp-content.md) and `009`'s FR-009 and plan table
point at it. Ten tests in `McpArtefactTests`, whole suite 1200/1200 in `Release` at 0 warnings, and the round's
diff reaches none of the four paths it may not touch. Proven live against `npx -y @playwright/mcp@latest`
(`--browser msedge`): a screenshot of `https://example.com` landed as
`<workspace>/.sharpcoreagent/artifacts/npx-browser_take_screenshot-1.png`, 44 912 bytes, PNG signature intact.
Measurements and the three things the live run taught are in the plan's `## As built`.

**Spec**: [`010-browser/spec.md`](010-browser/spec.md). The paragraph below is the round's promise; the
spec is the contract that refines it, and it is the one to read before the plan. The plan is
[`010-browser/plan.md`](010-browser/plan.md) (2026-10-30): one sink, one directory, no new flag — a `save`
delegate threaded into `McpContent.Flatten`, an `McpArtefactSink` that owns approval, ceilings, MIME and IO,
and two ceilings on `McpClientOptions`. It lands as `tasks.md`, `contracts/mcp-content.md` and the tests,
in that order.

**Ships**: the page a browser server returns. `--mcp "npx -y @playwright/mcp@latest"` already drives a
browser today, and the model can already read the DOM it hands back; what it cannot do is see. Round 4
takes an `image` or a `blob` in `content[]`, decodes it under a byte ceiling, writes it under
`<workspace>/.sharpcoreagent/artifacts/` — the spec's reading of "the session directory", because
`--session` may point outside the workspace and a report can only name a relative path — as
`<server>-<tool>-<n>.<ext>` (extension from the MIME type), and answers with the path and the size —
beside the description `Flatten` already returns, so a run that cannot write still degrades to a
sentence rather than a failure. It also reports what it wrote: `browser_take_screenshot` is annotated
`readOnly`, so `McpTool` reports no effect for it today, and a turn that left a PNG in the workspace
says it changed nothing (spec FR-008). Plus the documentation: which servers are known to work, what the command
line looks like, and that the browser itself is the developer's to install.

**Does not ship**: a bundled browser, a driver manager, a JavaScript engine of our own, a `browser` tool
of ours, a `--browser` flag (`Decision record`, 2026-10-28).

## Round 5 — `011-subagent-task` (landed 2026-10-30 as `a447a4d`)

**Status**: landed, all four phases. `task` is registered in `AddTools` beside `dotnet_verify`, so a default build
advertises seventeen tools and `--web` makes eighteen; the shipped prompt carries rule 8; and the contract is
`specs/011-subagent-task/contracts/task-tool.md`, which the tool catalogue in `001` now points at. The whole suite is
1190/1190 in `Debug` and again in `Release`, both builds at 0 warnings and 0 errors — it was 1167 — and this round added
no line to `Domain`, `CodingAgent`, `Domain/Index` or `SharpCoreAgent.Indexing`: the four-path diff is traced line for
line in `tasks.md` (`T020`), which is also where the one caveat lives, that `HEAD` predated `012` and `015` at the
time of writing and both have since landed in `a447a4d`.

**Ships**: `task` promoted from `TaskTool.cs.pending` to a registered tool. A sub-agent gets the same
model, a child registry without `task` (no recursion), `SubAgentPrompt` as its system prompt, and an
approval mode that is `AllowAll` only when the parent is — otherwise `ReadOnly`, because a child that
can silently write is a parent that can silently write. The child's transcript is discarded; its final
message is the tool result, bounded like any other.

**The draft**: `TaskTool.cs.pending` moved into `specs/011-subagent-task/` unchanged in phase 1 and stayed
uncompiled while the landed tool was written beside it, which is the decision recorded when this round was planned. The
landed `TaskTool.cs` implements that design with the one correction a probe forced: the constructor takes
`(IChatModelResolver, Func<IToolRegistry>, AgentOptions)`, because a tool that took `IEnumerable<ITool>` cannot be part
of the set it is handed — the container reports a circular dependency and every host fails at startup (`F-1`). Reading
`IToolRegistry` through the factory is also what lets a child see `--mcp` tools, since `AddMcp` replaces the registry
after startup (`F-2`). The draft stays in that folder now that the round has closed: it is this round's design of
record, nothing compiles or loads it, and `tasks.md` `T001`–`T003` keep the measurements that corrected it. A future
round that wants the file gone is deleting a document, not a dependency.

**Evidence**: `TaskToolTests` 15/15 — arguments and their refusals, the header, the four failure sentences, the
approval matrix (a child of an asking parent is refused a write, a child of an allowing parent is not), and effect
forwarding (`Created`, `Modified`, `CommandRun`, `Deleted` in order, `Read` absent) — plus two `CodingAgentTests`
proving a delegated `.cs` edit is gated by the parent's verifier and closed by a `dotnet_verify` that passes after it;
the gate test fails at `Assert.True(report.IsUnverified)` when the forwarded `Modified` effect is filtered out. Then
three `TaskToolRegistrationTests` (the tool is in the registry, the seventeen names are the documented set, every tool
resolves without the circular dependency `F-1` was) and three `PromptLibraryTests` for rule 8 and the child's prompt.
One test moved for an honest reason: `ChatLoopTests.NearingTheCeilingSaysSoBeforeTheSessionStops` raised its ceiling
from 1 400 to 1 700 tokens, because the demo adapter bills characters and the prompt grew a rule.


## Round 6 — `013-evidence-mcp-server` (landed 2026-10-29)

**Status**: landed. It reverses a sentence in "What stays out of this program", the reversal is argued above, and
the constitution was amended to 1.2.0 for it. It shipped in the six phases its plan lists, and the measurement in
phase 5 is what decided whether the round was worth its weight: `mcp bench` put 138 723 characters of evidence
against 8 134 414 characters of cited source — a ratio of 0.017, or about 1/59th — and the same run put the p95 of
a `search_code` call at 4.4 s against SC-005's 300 ms. One criterion met beyond argument, one missed by an order of
magnitude and published anyway, which is the bargain this section made: a poor number is published with the round
rather than left out of it. SC-005 has since been restated in `spec.md` with that number beside it, and the work
the restatement does not do — a cheaper per-call lease — sits in the open-work index below.

**Ships**: `sharpcoreagent mcp serve` — a stdio MCP server for `--workspace` that publishes the five read-only
evidence tools (`search_code`, `find_symbol`, `find_references`, `code_context`, `index_status`) with the
schemas they already carry, so any agent that can launch a stdio server can ask this repository's index where
something is and receive line-addressed, hash-addressed evidence. Plus `mcp config --client`, which prints the
snippet for Claude Code, VS Code or Codex; a bounded refresh so the answers track the working tree; a
published-set gate that no flag can widen; and `mcp bench`, which measures the evidence-to-source ratio and
the answer latency rather than quoting someone else's percentage.

**Does not ship**: HTTP or SSE transports, authentication, resources, prompts, sampling, logging, a
`--read-tools` flag or any write, shell or network tool, more than one workspace per process, a `browser` or
`web_search` surface. Round 4 and round 5 are untouched and can land before or after it.

**Depends on**: `002` (landed) for the index and the tools. Not on round 3: this is the server side of the
protocol, and the client half is only used by a test, which is exactly what makes that test worth having.

**Evidence**: our own `--mcp` client starts the server, lists `sharpcoreagent_search_code`, calls it, and gets
text byte-identical to the in-process answer; a registry-wide test proves `write_file` and its siblings are
unreachable; and the ratio `mcp bench` prints is pasted into the round's `plan.md` — including if it is
unflattering.

## Round 7 — `015-fsharp-peer` (landed 2026-10-30 as `a447a4d`)

**Status**: landed, all four phases, whole suite 1167/1167 at 0 warnings with zero diff on the index. Every tool this
product shipped read bytes — `search_text` matches names, `find_references` follows the index's typed edges,
`dotnet_verify` asks `dotnet build` for a verdict — and none could answer *where else is this symbol used* or *what
does this call take, here*. The index cannot answer the first honestly (a method and a field called `Total` on
different types are one symbol to it) and a build cannot answer the second at all. F# is where this happens first
because `FSharp.Compiler.Service` is the SDK's own front end published as a library: no server to start, no protocol
to speak, no second process to supervise, so the round keeps the rule that `Process.Start` lives in exactly two
places while shipping real code intelligence.

**Ships**: twelve ports and records in `Domain/Code`; the `SharpCoreAgent.Languages.FSharp` adapter over
`FSharp.Compiler.Service`; `LanguageRouter`, the only type that knows an adapter list exists, choosing one from the
path's extension alone; and three language-neutral tools `--fsharp` registers — `file_diagnostics` (a path),
`uses_at` and `signature_at` (line and column). The tools are handed Domain ports and never learn that F# exists. A
failure is a value rather than an exception, `CodeFailure` carrying `FSH001`–`FSH006` from the adapter and
`LANG001`/`LANG002` from the router with a hint naming a registered tool. Positions use the editor's convention — a
1-based line and a 0-based column, printed as `path:line:column`, adjusted by nothing. The capability is off unless
asked for (`--fsharp` / `SharpCoreAgentOptions.FSharpToolsEnabled`), the prompt rule `PromptLibrary.FSharpRule` ships
with the tools or not at all, and `dotnet_verify` stays the only gate — a clean `file_diagnostics` does not mark a
turn verified. A `.fs` edit is gated exactly like a `.cs` one.

**Does not ship**: C# and VB — Roslyn is a bigger surface and `HeuristicSyntaxFactsExtractor` is not the place to grow
one; `.fsx` scripts, since the adapter answers about a project's compile list and a script has none; edits, fixes and
formatting, because the ports are read-only by construction; a `dotnet` child process, MSBuild or a restore, all three
of which are what `dotnet_verify` is for and a third `Process.Start` this product does not want; XML documentation at
a position, because `GetToolTip` is unusable in the shipped checker (the `tokenTag` path throws or returns empty), so
`SignatureInfo` is `(Display, Parameters, ActiveParameter)` and the round says so rather than shipping a field that is
always empty.

**Depends on**: `002` (landed) for the tools' neighbours and the house style of a retrieval answer; `012` (landed) for
`dotnet_verify`, which stays the authority this round defers to and never replaces.

**Evidence**: 31 new tests — `LanguageRouterTests` (6), `CodeIntelligenceToolTests` (10),
`FSharpToolRegistrationTests` (4), `FSharpToolTests` (4, end to end through the real adapter), `FSharpOptionTests`
(3), two prompt tests and the two gate tests. The gate is proved by deleting `".fs"` from
`VerifyGate.DotnetExtensions` and watching the new test fail. Phase 0's measurements held in the product: the first
question about a project costs seconds, every later question on the same instance costs milliseconds (3.5–5.2 s cold,
2 ms warm, one adapter instance per process). Zero warnings; the whole suite 1167/1167.

## Round 8 — `016-model-roles` (landed 2026-10-31 as `6ac0c90`)

**Status**: landed, whole suite 1228/1228 in `Release` at 0 warnings in the tree this commit carries. A session
used one model for everything; this round gives the three jobs a session does — planning, acting and reviewing —
a model each, and it closes `006`'s `T016` by offering a missing key as a paste instead of refusing the first run.
No line was added to `Domain`: a role is a resolution in `CodingAgent`, not a new kind of message. The contract is
[`016-model-roles/contracts/cli-roles.md`](016-model-roles/contracts/cli-roles.md); the argument is
[`016-model-roles/spec.md`](016-model-roles/spec.md) and the measurements are in its `plan.md`.

**Ships**: `--planner` / `--executor` / `--reviewer <model>` — and the settings keys and
`SHARPCOREAGENT_PLANNER_MODEL` / `_EXECUTOR_MODEL` / `_REVIEWER_MODEL` variables of the same names — name the model
for a plan turn, an act turn and `/review`. A value resolves through the catalogue `/model` uses: `provider:id`, an
alias, or a bare id that keeps the provider in force at the end of the command line, so flag order cannot change its
meaning. `AgentSession.SessionModel` records what the developer chose, `ChooseModel` is the one writer of it (and
what `/model` calls), and a role switch reads it as its fallback, so a plan turn on the planner's model followed by
`/mode act` returns to the chosen model instead of leaving the planner's in place. `/roles` prints the three and
says which of them is simply the session model. `/review [focus]` sends `PromptLibrary.ReviewInstruction` as one
turn on the reviewer's model with approval read-only, then restores the mode and the model. The preflight `004`
built for the fallback now covers every model the process may use — the session's, each named role's and the
fallback's — so a missing key on a named role is an exit `3` at startup rather than a failed turn an hour later. On
a terminal at both ends with nothing configured, that missing key is a prompt instead: read with the echo off, never
printed, written as `NAME=value` to the file `--credential-file` or `SHARPCOREAGENT_CREDENTIAL_FILE` names, or to
`credentials.env` beside the settings document — outside every workspace, because a key is not a repository's
property — keeping every other line, owner-only where the platform has file modes, and recorded in the settings
document when the developer named no file so the next run reads it too.

**Does not ship**: rotation, revocation or expiry of keys — the file is a `NAME=value` document the developer edits;
per-role budgets, temperatures or approval modes, because a role names a model and nothing else; a fourth role or a
role per tool; `config edit` (`006` `T017`) and per-provider sections (`006` `T018`).

**Depends on**: `001` (the loop and the CLI), `004` (the fallback, which taught the preflight to check a second
model), `005` (`--credential-file` and its variable) and `006` (the settings document, `config`, and `T016`, which
this round closes).

**Evidence**: 28 tests in four new classes, with no existing test changed — `AgentRoleOptionTests` (parsing, the
bare-id rule, resolution), `ChatRoleTests` (`/roles`, the mode switch, `/review` restoring the approval mode and the
model), `CredentialPromptTests` (the paste, the refusal, the default file) and `CredentialFileUpsertTests` (the
write, the preserved lines, the replaced assignment). Zero warnings; the whole suite 1228/1228.

## Round 9 — `017-image-view` (landed 2026-10-31 as `e69cbdc`)

**Status**: landed, whole suite 1256/1256 in `Release` at 0 warnings, on `main` in one commit and with no merge
commit — the shape round 6 took. Round 4's T035 asked for this round and refused to fake it: *"a screenshot the model
could \*see\* is a content-block change in `Domain.Conversation`, which is its own round."* So the round changes
`Domain.Conversation`: `ImageContent` (media type, base64, `ByteCount`, `ToDataUri`), `ChatMessage.Images` on the user
role, `ToolResult.Images`, and `ModelDescriptor.SupportsImages` as a declared capability. The contract is
[`017-image-view/contracts/view-image.md`](017-image-view/contracts/view-image.md); the argument is
[`017-image-view/spec.md`](017-image-view/spec.md) and the measurements are in its `plan.md`.

**Ships**: `view_image` — an eighteenth default tool, `ReadOnly`, one `path`, five extensions
(`.png`/`.jpg`/`.jpeg`/`.gif`/`.webp`), a 4 MiB ceiling, and a capability gate that refuses *before* opening the file
when the session's model is not declared to read images, naming the model and forbidding a description of a picture
it never saw. The loop attaches what the tool returned to a user message carrying `PromptLibrary.ImageAttached`,
because the tool role carries text only on every compatible provider — so the transcript reads: the call, the
sentence, the picture. The wire is an `image_url` part with a `data:` URI, and a message with no images is still a
plain string, so no earlier request shape changed. `models` prints `images=yes|no`, and the catalogues say which
entries can see (Grok's `grok-4.7`/`grok-4`/`grok-3`, every `openai-compatible` id, none of DeepSeek's).

**Does not ship**: images inside MCP results (`McpContent.Flatten` is unchanged, so a browser screenshot is shown by
calling `view_image` on the path the sink returned), `fetch_url` returning images, OCR or any local vision, PDF/SVG/
audio/video, pasted or piped images and an `--image` flag, resizing or re-encoding, images in session files, a
per-turn image budget or deduplication, an image token estimate, and serving `view_image` over `mcp serve`.

**Evidence**: `ViewImageToolTests` (eleven methods, one a six-case theory over the media types), eight other classes
extended — `CodingAgentTests` (the picture arrives on the next user message, after the tool message, and is in the
next request), `OpenAiCompatibleTransportTests` (parts vs string), `ConversationImageTests` (the Domain surface,
including `ToString` never printing the payload), `ModelCatalogTests`, `PromptLibraryTests`, `TaskToolRegistrationTests`
(eighteen names), `McpServerPublishedSetTests` (`view_image` registered, not published) — and
`ChatLoopTests`' ceiling moved 1 700 → 2 000 tokens because the prompt grew rule 9. Zero warnings, 1256 passing.

## Open work, and what is still undecided

Every round shipped with lines left open on purpose. They are recorded in that round's `tasks.md`, which
is where the argument lives; this section is the index, so nothing has to be found by grepping for an
empty checkbox.

| Round | Open | Why it is still open |
|---|---|---|
| 0 | `002` T034 | the cold build and the index inside their stated budgets |
| 1 | `007` T030–T032 | a writable attachment, an indexed attachment, persisted attachments — each a specification of its own, and T032 is refused on principle rather than deferred |
| 1 | `003` T032–T034 | `--resume`'s listing, git checkpoints with `/undo`, automatic compaction — three console features that need a shape this product has not chosen |
| 1 | `003` T037 | a live re-check of both model catalogues after the providers' next revision |
| 2 | `008` T031–T033 | paging a long page and `robots.txt`, refused rather than deferred — no transcript has hit the character ceiling, and politeness is not a second request per fetch — plus a web search tool, which is the one decision below |
| 3 | `009` T034–T037 | resources, prompts, sampling, logging, remote transports, server mode, a declarative server list |
| 4 | `010` T031–T034 | a browser of our own, PDF/video/audio artefacts, retention and deduplication, a catalogue of what was captured — T035 (*showing the screenshot to the model as an image*) is closed by `017` |
| 6 | `013` SC-005 → the index lease | the served call costs 4.4 s at p95 against a 300 ms budget: the hop costs nothing and the per-call lease on a 233 MiB index costs the rest. The criterion is restated with that number beside it (2026-10-29, `spec.md`); what is still open is the work the restatement does not do — making the lease cheap enough that the budget could be met, which is index work (`002`) rather than MCP work, and needs a measurement of its own |
| 9 | `017` T024–T033 | images inside MCP results (a screenshot is a file, and `view_image` can name it), `fetch_url` returning images, OCR and local vision, PDF/SVG/audio/video, pasted or piped images and an `--image` flag, resizing and re-encoding, images in session files, a per-turn image budget or deduplication, an image token estimate, and serving `view_image` over `mcp serve` |
| — | `005` T012, T013 | whether `--credential-file` takes a directory, and whether `credentials --check` exists |
| — | `006` T017–T018 | `config edit` and per-provider sections. T016 — a first-run key prompt with restricted permissions — is closed by `016`, which ships the paste, the no-echo read, the owner-only file beside the settings document and the settings entry that makes the next run find it; rotation stays out of scope there and is named in `016`'s spec |

The one capability this program has not decided to build is `web_search`: it needs a provider and a key,
and a key is a decision with an owner. It stays unnumbered; if it is decided, it becomes `018` and it
changes no round above it. `012` is the compiler oracle, which needs neither a provider nor a key, `015` is
F# code intelligence, `016` is model roles and `017` is the image view.

## What stays out of this program

Checkpoints and rewind, rules files (`AGENTS.md`, `.clinerules`), remote or team sessions, a plugin
marketplace. Each is a real capability and each is a separate program; naming them here is what keeps this
one finishable.

MCP *server* mode used to be on that list. It moved to round 6 (accepted 2026-10-28) for one reason and
with one limit: what the server publishes is the index's evidence, not the machine. If a later round wants it
to publish a tool that writes, starts a process or reaches the network, that is not an extension of round 6 —
it is a different program, and it goes back on this list.

