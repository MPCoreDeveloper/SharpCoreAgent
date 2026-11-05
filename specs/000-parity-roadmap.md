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
| 10 | [`019-image-ingress`](019-image-ingress/spec.md) | a picture reaches the model through every door already open — an MCP `image` part and an `image/*` response from `fetch_url`, behind one `ImagePayload` rule (landed 2026-10-31 as `83c28ea`; closes `017` T024–T025) | 0, `008`, `009`, `010` and `017` (all landed) |
| 11 | [`020-image-turn-budget`](020-image-turn-budget/spec.md) | the pictures one turn shows have a total — 16 MiB, counted by the loop and refused by each of the three doors with both numbers (landed 2026-10-31 as `051089d`; narrows `017` T031) | 0, `017` and `019` (landed) |
| 12 | [`021-image-dedup`](021-image-dedup/spec.md) | a picture the conversation already carries is not attached again — one rule for the three doors, answered as a success rather than a refusal (landed 2026-10-31 as `5a01290`; closes `017` T031's deduplication clause) | 0, `017`, `019` and `020` (landed) |
| 13 | [`022-ide-client-config`](022-ide-client-config/spec.md) | `mcp config --client visualstudio`, and `--write` — the block merged into the one `.mcp.json` two IDEs read, instead of a redirect that truncates (landed 2026-11-02 as `ab07988`: `McpClientKind.VisualStudio`, `McpClientSnippets.TryShape`/`Entry`, `McpConfigWriter`, 25 tests) | 0, and `013` (landed) |
| 14 | [`023-ide-surface`](023-ide-surface/spec.md) | `ui --serve` — the index as a page on `127.0.0.1`, from one builder both it and `index --status --json` read (landed 2026-11-02 as `1eb0164`: `UiCommand`, `UiServer`, `UiPage`, `UiEndpoint`, `IndexStatusDocument`, 62 tests) | 0, `002` and `013` (landed) |
| 15 | [`024-ide-agent-definition`](024-ide-agent-definition/spec.md) | `ide agent` and `ide tasks` — the profile that puts this server's five tools in the editor's agent picker, and the task that rebuilds the index, both written only where they may be | 0, `013` and `022` (landed) |
| 16 | [`025-served-index-snapshot`](025-served-index-snapshot/spec.md) | the served call stops reloading what it already knew — one loaded build and one computed status per index, shared across the handles the lease opens and releases, and a harness that says which call its p95 describes (landed 2026-11-02 as `37c5efa`: `IndexReadCache`, `IndexReadState`, `IndexStatusMemo`, `FileStamp`, the bench's warm-up and `cold`, 11 tests) | 0, `002` and `013` (all landed) |
| 17 | [`026-checkpointed-index-writes`](026-checkpointed-index-writes/spec.md) | one commit per checkpoint of 32 files — a rebuild of this index falls from 17 m 51 s to 2 m 53 s and from 700 commits to 26, and the clusterer becomes 74 % of what is left (landed 2026-11-02 as `acf125e`: the checkpoint in `IndexFileWriter`, four boundaries in `IndexWriter`, `PruneAsync`'s one commit, `IndexBuildReport.Commits`, the `--verbose` row, 11 tests) | 16, and `002` (landed) |
| 18 | [`027-community-pass-steps`](027-community-pass-steps/spec.md) | the clusterer's two minutes split into five steps — it is the 7 305-statement `assign` loop (1 m 21 s) and Louvain (53.4 s), not the 0.3 s of graph reads, and a commit nobody counted is counted (landed 2026-11-02 as `97294a3`: five nested names in `IndexPhases`, a `Func<Task<T>>` overload on the clock, `CommunityAssigner`'s five steps and its commit, 4 tests) | 17, and `002` (landed) |
| 19 | [`028-row-addressed-symbol-writes`](028-row-addressed-symbol-writes/spec.md) | the clusterer writes both columns by primary key instead of issuing 7 305 `UPDATE` statements — `assign` falls from 1 m 21 s to 1.8 s, `communities` from 2 m 15 s to 1 m 10 s, and `louvain` becomes the run (landed 2026-11-03 as `96de2e4`: `CommunityAssigner.Write` on `IndexSql.Update`, the statement deleted, 4 tests) | 18, and `002` (landed) |
| 20 | [`029-modularity-partition`](029-modularity-partition/spec.md) | the clusterer stops calling the store's `LouvainAlgorithm` and partitions with modularity it owns — `louvain` falls from 1 m 8 s to 0.1 s, `communities` from 1 m 10 s to 2.6 s, a rebuild from 1 m 56 s to 55.6 s, and the partition rises from 0.490230 to 0.655454 by the definition (landed 2026-11-03 as `e339564`: `ModularityPartition`, `Rank` in place of `RankAsync`, 6 tests, three probes deleted) | 19, and `002` (landed) |
| 21 | [`030-index-compaction`](030-index-compaction/spec.md) | the index file stops being 10.8× the rows it holds — the store's full vacuum gives back 714.6 MiB of 787.9 MiB in 0.6 s with every row intact, a finished rebuild now does it, and `index --compact` does it on demand (landed 2026-11-03 as `caa392b`: `IndexCompaction`, `CompactAsync`, the rebuild hook, the `--compact` block and its `--json`, 8 tests, four probes deleted) | 20, and `002` (landed) |
| 22 | [`031-cheap-no-op-refresh`](031-cheap-no-op-refresh/spec.md) | the pass that changed nothing stops paying for it — a refresh on an untouched workspace falls from **5.5 s to 507–662 ms** and commits nothing, because its two scans were prefetching the `meta` batch it did not need to write (landed 2026-11-03 as `ebd2587`: `IndexIdSequence.For`, `Describes` in `WriteMeta`, the guarded recount in `PruneAsync`, `GetStatus(countOrphans:)`, 2 tests, three probe rounds deleted) | 21, and `002` (landed) |
| 23 | [`032-write-only-what-moved`](032-write-only-what-moved/spec.md) | the community pass stops rewriting rows that already say what it computed — one changed file falls from **12.2 s and 3 commits to 9.2 s and 2**, with `assign` **2.4 s → 0.0 s**, because the pass rewrote all 7 305 declarations it had never read (landed 2026-11-03 as `5ea10ee`: `CommunityAssigner` reads both columns it owns, `Write` batches only the rows that moved, `SameDegree`, `IndexSql.AsDouble`, 2 tests, one probe deleted) | 22, and `002` (landed) |

`web_search` is decided against rather than deferred: a native search tool is a ranking, a scraping and a
rate-limit policy this repository does not own, and `--mcp` already reaches an official search server —
Brave, or any equivalent (decision record below, 2026-10-31). `018` was held for it and is now **released**:
unassigned rather than reserved, so the next round that needs a number may take it. (`012` went to the
compiler oracle — [`012-compiler-oracle`](012-compiler-oracle/spec.md) — which needs neither a provider nor a
key, `013` is the evidence server, `015` is F# code intelligence —
[`015-fsharp-peer`](015-fsharp-peer/spec.md) — which links the F# front end in rather than talking to a
server, `016` is model roles, `017` is the image view, `019` is image ingress — round 10 — `020` is the turn's image
budget — round 11 — `021` is image deduplication — round 12 — `022` is the IDE's client configuration — round 13 —
`023` is the IDE surface itself, a page for the developer rather than for another agent — round 14 — and
`024` is the agent definition, the profile such an editor lists and the task such a workspace runs — round 15 —
`025` is the served call's own memory, the build and the status a `tools/call` no longer rebuilds — round 16 — and
`026` is the commit interval, the write phase a build no longer pays once per file — round 17 — `027` is the clusterer's
own steps, the five decisions that were hiding inside one phase — round 18 — and `028` is the write-back itself, the
7 305 statements a pass no longer parses — round 19 —
while `014` was never assigned and `018` stays released, unassigned for a round that wants it.)

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
- **There is no `web_search` tool, and `018` is released.** Decided 2026-10-31, after round 12. A search tool
  is three policies in one — what is worth ranking, what may be scraped, and how often — and none of them is
  this repository's to set: the first one written down is a vendor with a key and a price, and the vendor
  list changes faster than a round does. `--mcp` is the answer, in the shape round 4 chose for a browser: the
  developer names an official search server (Brave's is the one the README carries), keeps the key in their
  own environment, and the tools arrive under that server's own names in the same registry as everything
  else. Nothing here refuses a model an answer — `fetch_url` still reads a page when one is named — only a
  ranking it did not ask its operator for. It would reverse if a search provider were pinned and shipped the
  way `--fsharp` pins a compiler: a different decision, with a different owner, and not this round's.

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

**Does not ship**: JavaScript execution, cookies, logins, screenshots, a search engine — the decision record's entry,
2026-10-31, is where search went instead.

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

**T036 amendment (2026-11-05).** A server that exits after startup is now restarted on the next
call, never during the failed call, at most `McpClientOptions.MaxRestarts` times (default three), with
a fresh `initialize` and `tools/list`. The restart budget, the no-replay rule and the four cases a
real child process can exercise are pinned in `McpProcessTests`; the old "a dead server stays dead"
refusal is gone from `spec.md`.

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
calling `view_image` on the path the sink returned — closed by `019`, which attaches the part and keeps the sentence),
`fetch_url` returning images (also `019`), OCR or any local vision, PDF/SVG/audio/video, pasted or piped images and an
`--image` flag, resizing or re-encoding, images in session files, a per-turn image budget or deduplication, an image
token estimate, and serving `view_image` over `mcp serve`.

**Evidence**: `ViewImageToolTests` (eleven methods, one a six-case theory over the media types), eight other classes
extended — `CodingAgentTests` (the picture arrives on the next user message, after the tool message, and is in the
next request), `OpenAiCompatibleTransportTests` (parts vs string), `ConversationImageTests` (the Domain surface,
including `ToString` never printing the payload), `ModelCatalogTests`, `PromptLibraryTests`, `TaskToolRegistrationTests`
(eighteen names), `McpServerPublishedSetTests` (`view_image` registered, not published) — and
`ChatLoopTests`' ceiling moved 1 700 → 2 000 tokens because the prompt grew rule 9. Zero warnings, 1256 passing.

## Round 10 — `019-image-ingress` (landed 2026-10-31 as `83c28ea`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9 took. Round 9 made a
picture viewable and left two doors describing one: `McpContent.Flatten` rendered an `image` part as
`[image: image/png · 36,912 base64 characters]`, and `fetch_url` refused an `image/*` body by name. The workaround for
the first was a second tool call on the file the artefact sink had already written; for the second there was none,
because nothing was written at all. The argument is [`019-image-ingress/spec.md`](019-image-ingress/spec.md) and the
decisions are in its `plan.md`. The number is `019` because `018` was held for `web_search` then — released 2026-10-31,
when the tool was decided against — and `014` was never assigned.

**Ships**: `ImagePayload` (`Infrastructure/Images`) — one normalisation table (five spellings onto four types,
`image/jpg` → `image/jpeg`), one size estimate (the base64 rule the sink has used since round 4, extracted so the two
ceilings cannot drift apart), and one closed list that keeps `image/bmp`, `image/svg+xml` and `image/tiff` out of a
request body a provider would reject. `McpCallResult.Images` and `McpContent.FlattenWithImages` carry a server's image
parts beside the text that describes them, and `McpTool` attaches what qualifies: at most four per result, every
un-attached picture named with its reason, and a session whose model is declared text-only told once that the pictures
were not attached rather than left to describe them. `fetch_url` returns a picture for the four provider types when the
body arrived whole, it fits inside 4 MiB, and the session's model can see it; it refuses a body cut at its own 2 MiB
ceiling rather than sending half a PNG, and its old promise that nothing binary is ever downloaded is reworded rather
than quietly broken. `PromptLibrary.ImagesAttached` is the plural hand-over, and `ImageAttached` was reworded to name
the rule — call the tool again, `view_image` if the picture is a file — because a third door made "call `view_image`
again" false for two of them.

**Does not ship**: OCR or local vision, downscaling and re-encoding, PDF/SVG/audio/video, MCP `resource` blobs and
`resource_link`s (still a path `view_image` can open), pasted or piped images, `Accept: image/*` negotiation, images in
session files, deduplication or a token estimate (the per-turn budget this round refused is closed by `020`, which
found it to be a request-size question rather than a compaction one), and anything served over `mcp serve`.

**Evidence**: `ImagePayloadTests` (26 cases: two theories, the estimate, the ceiling boundary, both refusals) plus four
methods in `McpToolTests`, five in `FetchUrlToolTests`, one in `CodingAgentTests` and one in `PromptLibraryTests` —
thirty-seven cases in all, and the single assertion that quoted round 2's old refusal moved with the sentence. Build 0
warnings; suite 1,293 passed, 0 failed, which is the figure `README.md` carries.

## Round 11 — `020-image-turn-budget` (landed 2026-10-31 as `051089d`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6, 9 and 10 took. Round 10 gave
every door a ceiling and left the turn without one: one picture per `view_image` call, four per tool result, 4 MiB per
picture. Those bound a result; the number that decides whether a provider accepts a request is the sum, because an
attached picture is re-sent on every request that follows it. A browser turn of fifteen rounds, four pictures each,
ended with sixty pictures — 240 MiB of picture, 320 MB of base64 — in its last request, refused by the provider with a
status code whose body says nothing about images. The argument is
[`020-image-turn-budget/spec.md`](020-image-turn-budget/spec.md) and the decisions are in its `plan.md`.

**Ships**: `ImageBudget` (`Infrastructure/Images`) — one predicate and one sentence, and the only place the three
doors agree on either: both numbers, and the way out. `ToolInvocationContext.ImageBytesRemaining` (`long?`) is the
number a tool may still attach, null meaning nobody stated a budget, which is the same safe default `Model` takes and
what keeps every round-9 and round-10 answer byte-identical. `AgentOptions.MaxTurnImageBytes` is 16 MiB — 21.3 MB of
base64, inside the smallest request limit this program targets, and exactly four pictures at the per-image ceiling —
with zero as the documented off switch and 64 MiB as the ceiling on the ceiling. `CodingAgent.TurnState` sums what
actually travelled and hands each call the remainder, so the second tool of a round is judged against what the first
one spent; a new turn starts with all of it. `view_image` refuses before it opens the file, an MCP result stays a
success and appends one line per un-attached picture, and `fetch_url` fails with the header block intact — three
shapes each of them already had, one sentence between them.

**Does not ship**: transcript trimming and compaction (the ceiling bounds what one turn *adds*; earlier turns keep
their pictures, and `017` T031's remaining clauses are that gap), deduplication, an image token estimate, a flag or a
settings key for the ceiling, a ceiling that varies by model, and anything served over `mcp serve`.

**Evidence**: nine cases in five classes — `AgentOptionsTests` (the default, both edges, both refusals),
`ViewImageToolTests` (the `0 B` refusal and the exact-fit boundary, with an unstated budget still attaching),
`McpToolTests` (nothing left, and the in-result decrement), `FetchUrlToolTests` (8 KiB against a 1 KiB budget) and
`CodingAgentTests` (two calls in one round see ten then seven; a second turn sees ten again). Build 0 warnings; suite
1,302 passed, 0 failed, which is the figure `README.md` carries.

## Round 12 — `021-image-dedup` (landed 2026-10-31 as `5a01290`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–11 took. Round 11 bounded
what a turn may add and wrote down what it did not do, and the first item on that list was deduplication. So was the
first item on round 10's. `017` T031 has carried the clause since round 9: *two `view_image` calls on one file cost two
pictures*. A ceiling bounds that spend without reducing it, and round 11's own advice — ask again in a new turn — is
exactly what makes a repeated picture worse. The argument is [`021-image-dedup/spec.md`](021-image-dedup/spec.md) and the
decisions are in its `plan.md`.

**Ships**: `ToolInvocationContext.ImagesAlreadyShown` (`IReadOnlySet<ImageContent>?`) — the pictures the conversation
already carries, null meaning nobody said, which is the safe default that keeps every earlier expectation
byte-identical. `ImageShown` (`Infrastructure/Images`) is the one predicate and the one clause, beside the `ImageBudget`
that owns the turn's sentence. `CodingAgent.AlreadyShown` derives the set from `session.Messages` when a turn starts, and
`TurnState` adds each picture it records, so the second call of a round is told about the first — before any request has
carried it. The three doors answer a duplicate in the shapes they already had: `view_image` succeeds with a sentence and
no image, an MCP result keeps its success and appends one line per skipped picture (and per part the result itself
repeats), and `fetch_url` succeeds with the header block intact on the URL that answered.

**Does not ship**: transcript trimming and automatic compaction (the transcript still grows and stays grown; `/compact`
is the manual answer, and `003` T034 is the policy for when), a path-keyed or digest-keyed memory (identity is the
picture's type and bytes, because that is what a provider is charged by and what a model sees), deduplication across a
`/compact` that dropped the earlier message (the honest reading of "the conversation carries it"), an image token
estimate, and anything served over `mcp serve`.

**Evidence**: seven cases in four classes — `ViewImageToolTests` (the duplicate sentence with its `Read` effect kept,
different bytes still travelling, a duplicate beating a spent budget), `McpToolTests` (the conversation's picture named
rather than sent; an answer that repeats a picture attaching it once), `FetchUrlToolTests` (the same body twice, still a
success, still one effect) and `CodingAgentTests` (two calls in one round and then a second turn: nothing, then the
picture, then the picture). Build 0 warnings; suite 1,309 passed, 0 failed, which is the figure `README.md` carries. The
one ordering that changed is inside `view_image` — the file is read before the duplicate and the budget are checked,
because a duplicate cannot be recognised without its bytes — and no existing case observes it.

## Round 13 — `022-ide-client-config` (landed 2026-11-02)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–12 took. Rounds 1–12 made
this repository's index reachable from three *terminal* agents, and round 6 wrote down what it had not done: the three
clients it knew were Claude Code, VS Code and Codex. The fourth is the one that is not a terminal. Visual Studio 2026's
agent reads `.mcp.json` from the solution folder, under `servers`, with an explicit `"type": "stdio"` — a file and a key
no command named. `--client vscode` prints the right key for the wrong file, so the server appears in VS Code's list and
nowhere in the IDE's. Worse, `.mcp.json` is the one file two clients read under two different keys, and the documented
route into it was a redirect, which truncates: adding the second client deleted the first. The argument is
[`022-ide-client-config/spec.md`](022-ide-client-config/spec.md) and the facts, decisions and the two things this round
did not verify are in its `plan.md`.

**Ships**: `McpClientKind.VisualStudio` — `.mcp.json` in the workspace, `servers`, typed, with `vs` accepted — and
`mcp config --write`, which merges the block into the file instead of printing it. The merge keeps every top-level key,
every other server and every entry field the command does not own (`env`, `cwd`); it rewrites `type`/`command`/`args`,
because those are what go stale when the binary moves; an entry that already matches is not written to at all and says
so; and one committed `.mcp.json` carries both `mcpServers` and `servers` after two runs. A file that cannot be merged —
not JSON, not an object, a `servers` key that is an array — exits `7` with the file untouched. `McpClientSnippets.TryShape`
and `Entry` are the single place the key and the entry are decided, so the printer and the writer cannot disagree, and
`RejectWriteFlag` refuses `--write` everywhere else, `mcp serve` included.

**Does not ship**: a `.vscode/mcp.json` migration (that file is the fourth resort of Visual Studio's documented search
and a git-ignored `.vs\mcp.json` outranks both, but changing the file `--client vscode` names would invalidate every note
and script since round 6), a TOML merge (`--client codex --write` is refused rather than approximated), an agent
definition (`.github/agents/*.agent.md`, `.vscode/tasks.json` — the next slice), a `mcp config --check`, atomic-by-rename
writes, and any change to what `mcp serve` publishes.

**Evidence**: twenty-five cases in three classes — `McpConfigWriteTests` (the three outcomes, the two merges, the two
refusals), `McpConfigCommandTests` (`--client visualstudio`'s key, file and note; the note case now covers four clients)
and `McpConfigParsingTests` (four spellings, `--write` beside `--client`, `--client codex --write`, eight commands that
refuse it, and help/version). The five help-text cases that pin the block were amended, and the golden `Serve` section
grew the paragraph. Build 0 warnings; suite 1,356 passed, 0 failed — 25 this round's, on a tree that already carried 22
from the unrelated phase-timing slice in `002`.



## Round 14 — `023-ide-surface` (landed 2026-11-02 as `1eb0164`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–13 took. Rounds 6 and 13
made this repository's index reachable from other *agents*: three terminals, then an IDE's agent. What none of them did
was show it to the developer sitting in front of the editor, who could run `index --status` and read nine labelled lines,
or open one file at a time. `ui --serve` closes that: the same numbers, as a page, in whatever browser the editor already
has. The argument is [`023-ide-surface/spec.md`](023-ide-surface/spec.md); the probe that settled the platform question,
the ten decisions and the two things this round did not verify are in its `plan.md`.

**Ships**: `sharpcoreagent ui --serve` — binds `http://127.0.0.1:<port>/` on an ephemeral port (`--port 0` says the same
thing out loud), prints the URL as the first line of stderr, and serves `GET /` and `GET /api/status.json` until Ctrl+C
at `0`. `IndexStatusDocument` is the one builder of the twenty-key status object and the five formatters that decide how a
status reads (`State`, `TierName`, `Slash`, `Number`, `Size`), so `index --status --json` and the route are the same bytes
— asserted, not asserted-by-inspection. `UiPage` is a pure function of that document and self-contained: inline
stylesheet, both `prefers-color-scheme` blocks, one script, one fetch, no other origin. The default is `--status`
semantics (never writes, never walks) and `--refresh` is the opt-in, bounded by `--index-budget`. A foreign `Host` is
`403` and any other path or method is `404`, both with an empty body that carries no number, and every answer is
`no-store`.

**Does not ship**: a VS Code extension (`createWebviewPanel` needs a TypeScript project, `npm` and a `.vsix`), a Visual
Studio tool window (a separate spec with its own authentication story, because a remote-UI channel calls back into the
IDE), auto-opening a browser (Principle III names two `Process.Start` exceptions and a browser launch is neither), a
polling page, any write or tool or setting, and `--host`.

**Evidence**: sixty-two cases in four classes. `IndexStatusDocumentTests` (the twenty keys in the contract's order,
`null` for a build that never finished, and the byte-equality of the two renderings of one open index),
`UiPageTests` (every FR-009 field, the `missing` page with no card of zeroes, escaping, both colour schemes, no other
origin), `UiCommandParsingTests` (`--serve` required, `--port` range and spelling, `--refresh` off by default, FR-013's
eleven flags refused on the command and the panel's three refused elsewhere, and the note naming both editors), and
`UiServerTests` (one loopback round trip: both routes, a `POST`, a foreign `Host`, an unknown path, and a held port
reported rather than thrown — skipped rather than failed where the platform refuses the prefix). The `Panel` section of
the help text is golden, and the exit-code table gained the panel's `0` and `2`. Build 0 warnings; suite 1,418 passed,
0 failed, 0 skipped — 62 this round's, against 1,356.

The probe is part of the record rather than an anecdote: non-elevated on this machine `http://127.0.0.1:<port>/` binds,
`http://127.0.0.1:0/` is refused with code 87, a second listener on the same prefix is 183, a port a plain socket holds
is 32, and port 80 is access-denied at 5 — which is why `--port` checks `0–65535` and lets the bind decide.

## Round 15 — `024-ide-agent-definition` (landed 2026-11-02 as `db8ef91`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–14 took. Rounds 6 and 13 gave
other *agents* this repository's index; round 22 gave the IDE's own MCP client its file; round 23 gave the developer a
page. What none of them did was put the agent itself in the editor's picker, carrying a description and a tool list —
because a configured server is a set of tools, and an agent is the thing that decides to use them. `ide agent` does that;
`ide tasks` gives the same binary a keystroke in the task list. The argument is
[`024-ide-agent-definition/spec.md`](024-ide-agent-definition/spec.md); the two document formats, quoted from Microsoft's
references, the eleven decisions and the two things this round could not verify are in its `plan.md`.

**Ships**: `sharpcoreagent ide agent [--write]` — prints, or writes, `.github/agents/sharpcoreagent.agent.md`: a
`description`, a `tools` list naming the five published tools as `sharpcoreagent/<tool>`, and a prompt that tells the agent
to quote the `path:start-end` it was given and to report a missing index rather than guess. The tool list is built from
`McpServerCatalog.PublishedToolNames` — the same array `McpServerPublishedSet` filters by — so the profile cannot
advertise a tool the server stopped publishing. `--write` replaces the file whole only when the marker on its last line is
there, and refuses at `7` otherwise. `sharpcoreagent ide tasks [--write]` files one task, `sharpcoreagent: index`, whose
command is this binary's absolute path from the same `Invocation` answer the MCP blocks use, merged by label so a second
run replaces rather than duplicates. `--write` is the flag `022` added, renamed `Write` because two commands read it now;
`WorkspaceFileWriter` and its exception are `022`'s, widened.

**Does not ship**: `group`, `problemMatcher` and `type` in the task (the three keys the page describes only in prose, and a
guessed matcher is a file the editor rejects), `--force`, a prompt file, an instructions file, an agent skill,
`mcp-servers` inside the profile, Visual Studio 2026's own agent format, and the VS Code extension — which is still
`023`'s next round and still needs a TypeScript toolchain.

**Evidence**: fifty-four cases, all new except two amended where a message changed on purpose. `AgentProfileTests` (13:
the frontmatter's two keys and the four it decided against, the tool list equal to the published set and in order, every
tool named in the body, the marker last, one line ending throughout), `IdeCommandParsingTests` (25: both actions, no
action, an unknown one, an operand, and the flags that name a turn, a client, a network, an index action or the panel),
`IdeTasksWriteTests` (8: created, merged around another task and another key, replaced in place, unchanged, and the
comment dialect refused at `7`), `IdeAgentWriteTests` (6: created, unchanged, replaced, somebody else's file refused), and
two rows in `HelpTextTests`. Build 0 warnings; suite 1,472 passed, 0 failed, 0 skipped — 54 this round's, against 1,418.

The binary was run against this repository: the profile it wrote is committed, the task list it wrote was read and
deleted, because its `command` is one machine's absolute path and D8 said so before it happened.

## Round 16 — `025-served-index-snapshot` (landed 2026-11-02 as `37c5efa`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–15 took. Round 6 could
publish this repository's index but could not afford to serve it: a `tools/call` cost 3.2 s at p95 against a 300 ms
budget, and that round recorded the miss rather than moving the number, naming the mechanism in `013/plan.md`. This
round did the work that paragraph left open and found its other half. The argument is
[`025-served-index-snapshot/spec.md`](025-served-index-snapshot/spec.md); the two probes, their output and the nine
decisions are in its `plan.md`.

**Ships**: `IndexReadCache` — one `IndexReadState` per index database per process, holding the `IndexReader` whose
snapshot the five tools rank against and an `IndexStatusMemo` (a status plus the file stamp it was computed for).
`SharpCoreDbCodeIndex` takes both from it, and `Dispose` now closes the handle and keeps the answers, which is the
one line a reviewer should read twice. Both answers keep the keys they already had — `build_id` for the build, the
file's length and write time for the status — so a rebuild in this process or any other is seen by the next call; the
map is bounded at four workspaces, evicts the least recently used, and drops an entry whose database file is gone.
`mcp bench` gains one untimed call before its twelve samples and prints that call as `cold` beside the `p95`, because
with twelve samples the nearest-rank p95 *is* the slowest sample and the warm one was therefore always the cold one.

**Does not ship**: any change to `LeasedCodeIndex` or `IndexHandleCache` — the handle is still opened per call and
released in a `finally`, and the round's own test that proves a build in a terminal is never waiting on an idle
editor passes without an edit. No stamp-gated path that answers a *build* question without opening the database
(refused in the spec, with the reason). Nothing about the build: `002` T034, the 119–132 s writing run and the 61×
footprint, stays open.

**Evidence**: eleven cases, ten new and one amended. `IndexReadCacheTests` (10: one database one state, two databases
never one, the lease's shape sharing a state, `Forget`, the fifth evicting the least recently used, a deleted
workspace dropped on the next miss, a new instance seeing a build made in this process, a status read by the next
instance, a status recomputed when the file changes, invalidation reaching every reader), and `McpBenchTests`' new
`TheCallThatLoadsTheSnapshotIsReportedAndIsNotThePercentile` beside the amended summary row.
`CodeIndexOpenTests.SecondOpenReusesTheSameHandle` changed because a status no longer opens the database at all, which
is the change working rather than a casualty of it. Build 0 warnings; suite 1,483 passed, 0 failed, 0 skipped — 11
this round's, against 1,472.

The number is the point: `mcp bench` twice on this repository's index, 712 files and 13,686 passages, printed
`p95 261 ms · cold 2641 ms · parity 12/12` and `p95 248 ms · cold 2834 ms · parity 12/12`, against 3,199 ms before.
SC-005 is restated a third time in `013` and met; the cold call is published beside it rather than averaged into it.

## Round 17 — `026-checkpointed-index-writes` (landed 2026-11-02 as `acf125e`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–16 took. Round 17 is the
first of the three levers `002` D21 named, and the only one that is arithmetic rather than a judgement: D21 measured a
build's steps for the first time and found that 88 % of a rebuild was the store's own batch commit — 700 of them, one
per written file. The argument is [`026-checkpointed-index-writes/spec.md`](026-checkpointed-index-writes/spec.md); the
nine decisions, the terminal's output and both runs are in its `plan.md`.

**Ships**: a checkpoint in `IndexFileWriter` — `BeginCheckpoint`, `EndCheckpoint`, `AbandonCheckpoint`, a count of the
files a group is carrying, and a `RunInBatch` that joins an open group instead of opening its own batch. The batch is
opened lazily by the first write inside the group, so a group that carries nothing costs nothing and a build stopped
before its first file still pays exactly one commit. `IndexWriter` carries 32 files per checkpoint and closes it at the
end of the walk, before the budget's break and before cancellation leaves the writer, abandoning it on any other
exception; `RemoveVanished` and `PruneAsync` remove their whole set in one commit each; `IndexWriteResult.Commits` counts
every commit where it is timed, and `index --verbose` prints it beside the phase block it explains.

**Does not ship**: no `--checkpoint` flag (a decision the code can make once, not a knob), nothing about `communities`,
`meta` or the store's own commit, no parallel extraction, and no `--json` key — a commit count is a fact about the
writer's strategy rather than about the index a caller queries. One file is still written as one unit and `meta` is
still written last and alone, which is what keeps an interrupted build **stale** rather than wrong.

**Evidence**: eleven cases, all new. `CheckpointTests` (9): five files commit 3 times at the default and 5 at two files a
group, with the same counters and rows either way; an abandoned group leaves the store as the last commit left it and a
committed one survives a later failure; a zero budget commits only `meta`; a budget expiring inside a group keeps it
(200 files, 800 ms, rows equal to the counter, commits still one per group); an already-cancelled build writes nothing;
a prune of three files commits once; a second begin is refused. `IndexCommandTests` adds the `--verbose` row and the
absence of a `commits` key in `--json`. No existing test was edited — `PhaseTimingTests`, `RefreshBudgetTests`,
`VerifyPruneTests`, `WriterLockTests` and `IncrementalIndexTests` pass as they are. Build 0 warnings; suite 1,494
passed, 0 failed, 0 skipped — 11 this round's, against 1,483.

The number is the point: one `index --rebuild --verbose` on this repository's index, 740 files and 14,200 passages,
reports **26 commits** and **2 m 53 s**, where D21's rebuild reported 700 and 17 m 51 s. `write` fell from 15 m 47 s to
**36.9 s** and the commit inside it from 15 m 49 s to **37.5 s** — 25× both, and the two still agree to within a second,
which is D21's finding moved down rather than replaced. A seven-file writing run now costs 2 m 18 s, of which the clusterer
is 2 m 2 s. `mcp bench` still prints `p95 260 ms · cold 2774 ms · parity 12/12`, so round 16's SC-005 is untouched.

**Left open, with the arithmetic attached**: the interval itself. At 32 files a checkpoint the commit is still about half
of what the write phase costs, so a larger group would help — and a checkpoint is a buffer held in memory until it
commits, which is a decision about memory this round did not take. The next lever is `communities`, then the no-op
`meta`/open overhead, and `002` T034 stays open for SC-002's 5,000-file tree and SC-006's 61×.

## Round 18 — `027-community-pass-steps` (landed 2026-11-02 as `97294a3`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–17 took — and its records
followed in three more, as round 17's did. Round 18 is the *first half* of `002` D21's second lever: D22 ended by saying
`communities` was three quarters of a rebuild and a fixed charge per writing run, which is a phase and not an answer. This
round says which part of it costs the two minutes, and fixes nothing it measures. The argument is
[`027-community-pass-steps/spec.md`](027-community-pass-steps/spec.md); the seven decisions, the terminal's output and both
runs are in its `plan.md`.

**Ships**: five steps inside `communities` — `nodes`, `adjacency`, `louvain`, `degree`, `assign` — recorded on the build's
own clock, ordered after the phase they explain, all nested, and printed as `of which …` without a share of the run so the
block's arithmetic and its `other` line do not move. `IndexPhases.IsNested` becomes a lookup over six names instead of an
equality against `commit`. `CommunityAssigner.AssignAsync` takes the clock; `PartitionAsync` splits into `ReadGraph`,
`BuildGraph` and `RankAsync`, with the numbering that makes labels reproducible inside the partition step; and `Write`
charges its batch's two ends to `commit` and returns the one commit it made, which `IndexWriter` adds to the count.

**Does not ship**: nothing the pass computes. Same rows, same labels, same centrality, same counters. No incremental
clustering, no reuse of an unchanged file's label, no cheaper partition, no coarser graph, no flag, and no new public type —
`IndexPhase.Nested` already existed, and the five names are internal.

**Evidence**: four new cases. `IndexPhaseClockTests` pins the nested set, the order of the clusterer's names, and that a step
producing a value is timed across its await and hands the value back on both `Task<T>` and `ValueTask<T>`.
`PhaseTimingTests` pins the wiring: the five rows on a real build, `louvain` and `degree` at exactly zero when the corpus
declares no relation, the commit inside the phases that contain it, and the block still within the run. `CommunityAssignerTests`
pins the positive case — two clusters, all five nonzero and nested. `IndexCommandTests` pins thirteen phases in `--json` and
`of which nodes` on the terminal. Three counts in `CheckpointTests` moved by one, because the clusterer's commit is now
counted. Build 0 warnings; suite 1,498 passed, 0 failed, 0 skipped — 4 this round's, against 1,494.

The number is the point, and it is not the one the phase looked like. One `index --rebuild --verbose` on this repository's
index — 740 files, 14 231 passages, 7 305 symbols, 10 806 edges, 645 communities, 525.2 MiB — reports **3 m 2 s** and **27
commits**, and the phase reads:

> `communities` 2 m 15 s (74 %): `nodes` **0.1 s**, `adjacency` **0.2 s**, `louvain` **53.4 s**, `degree` **0.0 s**,
> `assign` **1 m 21 s**.

So the two candidates are the statement loop that writes 7 305 rows back (11 ms per `UPDATE` inside one batch, six times the
per-statement cost a file's mixed inserts pay) and the partition itself (53.4 s over a graph that `degree` crosses below the
report's resolution). The graph reads — the suspect a write-heavy history made most likely — are 0.2 % of the phase, and a
faster read path would buy nothing. An incremental run over the same index prints five zeros and one commit, so a skipped
phase is distinguishable from a cheap one.

Neither of the two candidates is proportional to what changed, and that is the sharper half of the finding: a run over
this index with **two changed files** costs **2 m 39 s**, because `assign` rewrites all 7 305 rows and `louvain`
re-partitions all 10 806 edges whatever the edit was. Round 17 could say a writing run pays a fixed charge; round 18 can
say it pays two, and name both.

**Left open, with the arithmetic attached**: `assign` is the larger lever and ours; `louvain` is the smaller one and the
store's, and answering it means a coarser graph or a bounded candidate set — a decision about label stability, and therefore
a spec of its own. Then the no-op `meta`/open overhead (5.5 s a turn), then the checkpoint's own size. `002` T034 stays open
for SC-002's 5,000-file tree and SC-006's 61×.


## Round 19 — `028-row-addressed-symbol-writes` (landed 2026-11-03 as `96de2e4`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–18 took — and its records
followed, as round 18's did. Round 19 answers the question round 18 asked and then acted on the answer: `assign` was 7 305
`UPDATE` statements at 11 ms each, and the store was not slow, it was parsing. The argument is
[`028-row-addressed-symbol-writes/spec.md`](028-row-addressed-symbol-writes/spec.md); the five decisions, the probe and both
runs are in its `plan.md`.

**Ships**: `CommunityAssigner.Write` hands each declaration to `IndexSql.Update` — the parser-free, primary-key write
`IndexFileWriter.WriteMeta` has used for `meta` since that table existed — inside the same one batch, with both ends still
charged to `commit` and the single commit still counted. `UpdateSymbol` loses its last user and is deleted, so no `UPDATE`
statement is left in `src/`. One dictionary per row, built in the loop, because the store may hold the reference until the
batch flushes.

**Does not ship**: nothing the pass computes, and nothing the store stores. Same rows, same label numbering, same
centrality, same 27 commits, same schema, same indexes, same `--json`. No flag, no public type, no dependency.

**Evidence**: four new cases. `IndexSqlUpdateTests` pins the three facts the change rests on — a row-addressed update inside
a batch is stored by `EndBatchUpdate`, is discarded by `CancelBatchUpdate` (the promise an interrupted pass makes), and
changes only the columns it names. `CommunityAssignerTests` gains the case at the pass's own level: through a rebuild every
declaration keeps its key, name, kind, line range and `qualified_name`, and the isolated declaration still gets an empty
label rather than an omitted column. Build 0 warnings; suite 1,502 passed, 0 failed, 0 skipped — 4 this round's, against
1,498. `index` reports `state fresh`; `index --verify` finds 761 rows for 761 files, 0 hash mismatches, 0 orphans.

The probe came before the code, and it is the round's whole justification: 3 001 declaration rows in one index, one batch
each way, the same values — **12 875–14 483 ms** through `ExecuteSQL` with bound parameters against **86–143 ms** through
the row API. The statement path formats every value into statement text, parses the text, and binds what it parsed, per
statement; the row path pays none of the three and keeps the index maintenance both share.

One `index --rebuild --verbose` on this repository's index — 761 files, 14 287 passages, 7 311 symbols, 10 840 edges, 623
communities, 525.2 MiB; **1 m 56 s** and 27 commits — reads:

> `communities` 1 m 10 s (61 %): `nodes` **0.0 s**, `adjacency` **0.0 s**, `louvain` **1 m 8 s**, `degree` **0.0 s**,
> `assign` **1.8 s**.

So `assign` fell 45× and `louvain` is now the phase and the run: 58 % of a rebuild, and the reason a run over two changed
files still costs what a full one costs. That is the store's, and making it smaller means a coarser graph or a bounded
candidate set — a decision about label stability, and therefore a spec of its own. The residual 1.8 s is 0.25 ms per row on
a thirteen-column table with four indexes, which is index maintenance and not a lever worth taking (`idx_symbols_community`
would have to be dropped, and every traversal query would then read differently).

**Left open, with the arithmetic attached**: `louvain` (1 m 8 s, and the fixed charge per writing run), then the no-op
`meta`/open overhead (5.5 s a turn, measured again at 6.0 s by this round's `index` run), then the checkpoint's own size.
`002` T034 stays open for SC-002's 5,000-file tree and SC-006's 61×.

## Round 20 — `029-modularity-partition` (landed 2026-11-03 as `e339564`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–19 took — and its records
followed, as round 19's did. Round 20 answers the question round 19 left standing: `louvain` was 1 m 8 s of a 1 m 56 s
rebuild and the whole fixed charge on a writing run, and it was not the graph that cost that. The argument is
[`029-modularity-partition/spec.md`](029-modularity-partition/spec.md); the five decisions, the three probes and both
runs are in its `plan.md`.

**Ships**: `ModularityPartition` in `SharpCoreAgent.Indexing/Graph` — modularity local moving on a weighted graph, a
coarsening that keeps each community's internal weight as a self loop, and a level loop that stops when a level merges
nothing — and `CommunityAssigner.Rank` calling it. The store's `CommunityDetection` namespace leaves the pass;
`DegreeCentrality` stays. The `louvain` step keeps its name, its nesting and its place in the report.

**Does not ship**: nothing the pass reads and nothing around it. Same graph (every relation, undirected, unweighted), same
`c0…` numbering by each community's ordinally-first member, same empty label for a declaration with no relation, same
schema, indexes, flags, `--json`, commit count and report shape. What is different is the granularity of the answer, and it
is published as a number rather than a footnote: 623 clusters of 6.3 declarations become 58 of 70 (59 on the graph as it
stood before this round's own five files were indexed), and 603 of the old ones sit whole inside a new one.

**Evidence**: six new cases in `ModularityPartitionTests` — two triangles apart, two triangles bridged (still two, which is
what separates a partition from a connected-components walk), one edge, a lone node, no single move improving the answer it
returns, the same graph twice, and twenty triangles in a ring merging below twenty — plus every existing case of
`CommunityAssignerTests` unmodified: two clusters, identical labels across two builds, the isolated declaration, and the
five measured steps with `louvain` and `degree` at exactly zero on a corpus with no relation. Build 0 warnings; suite
1,508 passed, 0 failed, 0 skipped — 6 this round's, against 1,502. `index` reports `state fresh`; `index --verify` finds
766 rows for 766 files, 0 hash mismatches, 0 orphans.

The probes came before the code and they are the round's whole justification. On this repository's own graph — 4 123
connected declarations, 10 744 edges, one connected component holding 3 999 of them — the store's `LouvainAlgorithm` costs
**71.100 s** over 12 iterations, because it recomputes modularity over the whole graph once per candidate move: 25 611
candidates a sweep, roughly 307 000 full-graph scans. Decomposing by connected component, the first idea, costs 1.7× less
and returns a different partition. Its own objective reports 1.001678 for its answer, which modularity cannot exceed — it
counts each internal edge twice — and scored by the definition it names, that answer is 0.490230. Ours is 0.0167 s at
0.475347 for one level and **0.1466 s at 0.655454** with the level above it, deterministic on repeat.

One `index --rebuild --verbose` on this repository's index — 766 files, 14 348 passages, 7 330 symbols, 10 867 edges, 58
communities, 787.9 MiB; run twice, identical; **55.6 s** and 27 commits — reads:

> `communities` 2.6 s (5 %): `nodes` **0.0 s**, `adjacency` **0.0 s**, `louvain` **0.1 s**, `degree` **0.0 s**,
> `assign` **2.4 s**.

So `louvain` fell 680×, `communities` fell 26×, and the run is now `write` (44.1 s) with the clusterer a footnote in it. A
writing run over one changed file costs **11.3 s**, of which `communities` **2.2 s** — against round 19's 1 m 10 s charge
for the same shape of run and the 5.8 s a run that changes nothing still costs. That is the number every round since 17
named, and it is no longer the clusterer's.

**Left open, with the arithmetic attached**: the index file is **787.9 MiB** where round 19 reported 525.2 MiB for the
same corpus, and `--rebuild` (twice) and `--prune` reproduce today's figure — the round does not explain it, does not claim

re-measured and unchanged), the `commit` line a writing run now consists of (7.0 s of its 11.3 s), and SC-002's 5,000-file
tree with SC-006's 61×.


## Round 21 — `030-index-compaction` (landed 2026-11-03 as `caa392b`)

**Status**: landed on `main` in one commit, with no merge commit — the shape rounds 6 and 9–20 took. Round 21 answers the
one number round 20 named and could not explain: an index file of **787.9 MiB** where round 19 measured 525.2 MiB for the
same corpus, reproduced by two rebuilds and by `--prune`. The argument is
[`030-index-compaction/spec.md`](030-index-compaction/spec.md); the five decisions and the four probes are in its
`plan.md`.

**Ships**: `IndexCompaction` and `ICodeIndexMaintainer.CompactAsync` — the store's `VacuumMode.Full` under the writer
lock, reporting the file before and after, the bytes reclaimed, the elapsed time, whether it ran and one note — the same
core called at the end of a rebuild that finished the workspace, and `index --compact` with a prose block and a `--json`
document. `--prune --compact` is one command line; `--status`/`--verify` beside it is refused; `--rebuild` refuses it too,
because a rebuild compacts by itself.

**Does not ship**: any compaction on a writing run or on the turn-start refresh. A rewrite costs what the live data costs
(0.6 s here, 0.02 s for a one-file index), and a turn's refresh budget is three seconds. No threshold and no schedule
either: there is nothing honest to trigger on, which is the paragraph below.

**Evidence**: `787.9 MiB → 73.2 MiB · reclaimed 714.6 MiB (91 % of the file)` in 0.6 s on a copy of the index this round
started with, with 14 368 passages, 7 336 symbols and 10 879 edges still in the file afterwards. On this repository the
same work reads `compacted 91.9 MiB in 0.5 s (165.4 MiB -> 73.5 MiB)`: a rebuild writes 165.4 MiB for 73.5 MiB of rows,
twice in a row, which is what the missing 714.6 MiB was made of. `index --verify` finds 771 rows for 771 files on disk
(766 before this round's own five files were added), 0 hash mismatches, 0 orphans, and a run that changes nothing still
costs 5.6 s with `meta` 2.0 s and 3.4 s of open — unchanged, because the commit is not size-bound. Four probes: the live
data is 73.2 MiB, 24 MB of it characters; `Full` reclaims 714.6 MiB where `Quick` and `Incremental` reclaim **0.0**;
`StorageStatistics` reports used 86 % / free 14 % before the rewrite and 85 % / 15 % after it, with fragmentation 0.0 in
both states, so no ratio in it can say "this file is ten times its rows"; and a one-file index grown by hand goes
6.1 → 14.1 MiB on 2 000 rows of 4 KiB, stays at 14.1 MiB after deleting all of them, and returns to 6.1 MiB in 0.02 s.

**Left open, with the arithmetic attached**: the writing-run floor, unchanged and now the whole of it — the `meta` batch
still costs 2.1 s after the rewrite against 2.9 s before, an open is 0.05 s either way, and round 20's 11.3 s for one
changed file is untouched. Then SC-002's 5,000-file tree and SC-006's 61×, and one new recorded fact nothing acts on: a
single-row `UpdateByPrimaryKey` costs **28.6 ms** on this store (`002` `research.md` D26) — the cost round 19 measured for
7 305 `UPDATE` statements, and three orders of magnitude above what a row update should be.

## Round 22 — `031-cheap-no-op-refresh` (landed 2026-11-03 as `ebd2587`)

**Status**: landed on 2026-11-03 as `ebd2587`. Round 22 takes the third of round 20's three numbers, the 5.5 s a run that
changes nothing cost, and finds a commit underneath it: the number was never a scan. The argument is
[`031-cheap-no-op-refresh/spec.md`](031-cheap-no-op-refresh/spec.md); the six decisions and the three probe rounds are in
its `plan.md`.

**Ships**: a no-op refresh that costs **507–662 ms** of a 645–769 ms wall and commits nothing. `IndexIdSequence.Next`
reads a table's `MAX(id)` on the first id that table hands out rather than six of them when the writer opens;
`IndexWriter.WriteMeta` borrows the counters the maintainer already loaded and opens no batch when every key but
`build_id` and `build_utc` already holds the value it would write (`Describes`); `PruneAsync` recounts only when it
removed a row; and the status a build reports about its own rows stops walking `chunks` for orphans, 2.6 s of the 3.1 s
`index --status` costs on this index. The status a *reader* asks for, and `--verify`, still count them.

**Does not ship**: any change to a pass that writes. A batch that changes a row still recounts four tables and still
commits — one changed file is **14.2 s with 12.8 s of it in `commit`** — and a prune that removes a file is **10.1 s**.
No new persisted state either: there is no "last checked" stamp, so the counters of a build that died before its `meta`
batch are repaired by the next pass that writes anything or by `--rebuild` (`002` `research.md` D27 records it).

**Evidence**: four `index` runs on this repository's 771-file index — **645–769 ms** wall, **507–662 ms** inside, `meta`
**1–2 ms**, `commit` **0** — against 5.5 s with a 5 107 ms commit measured mid-round and 1 774 ms for the `meta` batch
before the round's first change. Two `--prune` runs that remove nothing: **465–554 ms**. `--status` afterwards:
`fresh · built 2026-11-03T18:30:43Z · 771 indexed · 0 stale · 0 orphan rows` — the timestamp of the last build that
wrote, which is what the count is for. The suite is **1 518 green at 0 warnings**, with `CheckpointTests` at 11 and two
new cases in it. Three probe rounds explain how the target moved: `MAX(id)` over six tables was **3.6 s** at the open and
four `COUNT(*)` were **2 700 ms**; with the reads made lazy the `meta` phase became **5 109 ms** with **5 107 ms** inside
the commit; forcing the counts back on split it **5 235 / 2 537**. The counts were prefetching the batch, so the fix was
to not write it.

**Left open, with the arithmetic attached**: the writing run, which is now the whole of the floor — **14.2 s** for one
changed file with **12.8 s** in `commit`, spread over the row, edge, community and `meta` batches — and `--prune` at
**10.1 s** when it removes a file. With them on `002` T034: SC-002's 5 000-file tree, SC-006's **12.3×** (73.6 MiB of
file against 5.98 MB of tracked source, which is what round 21's vacuum left), and SC-003's p95, never measured.

## Round 23 — `032-write-only-what-moved` (landed 2026-11-03 as `5ea10ee`)

**Status**: landed on 2026-11-03 as `5ea10ee`. Round 23 takes the writing run round 22 left standing and gives back the
one commit in it that nothing needed: the pass that writes the community columns rewrote every declaration on every
writing run. The argument is
[`032-write-only-what-moved/spec.md`](032-write-only-what-moved/spec.md); the six decisions are in its `plan.md`.

**Ships**: a writing run over one changed file at **9.2 s and 2 commits**, against **12.2 s and 3**, with `assign`
**0.0 s** against 2.4 s. `CommunityAssigner` reads `community` and `centrality` beside the id and the key it already
read, compares the answer it computed with the answer the row holds, and opens a batch only for the rows that differ;
when none differ it returns `Commits` 0 without a batch, a commit or a row. The comparison is made in the form a reader
sees — a `null` label against the empty text an unclustered row reads back as, `Int32` and `DBNull` degrees through
`IndexSql.AsDouble` — and by bits rather than by a tolerance (`SameDegree`), because a tolerance would call a degree that
moved "unchanged" and leave the old value in the row.

**Does not ship**: the two commits that remain. The write batch is **6.7 s** of commit and `meta` **2.1 s**, and both
carry a row that changed: the probes priced the write commit at 7.2 s for a 103-line document and 7.6 s for a 700-line
source file, and 7.6 s again at 110.4 MiB, so commit cost follows the tables a batch touches rather than the rows it
carries. No automatic compaction either — the store grew **73.6 → 110.4 MiB** across four writing runs of one changed
file, which is why every number here was taken after `index --compact`.

**Evidence**: the baseline re-taken at the 73.8 MiB size round 21's vacuum leaves — **12.2 s** and **11.9 s** for one
changed document, `commits` 3, `commit` 11.2 s and 10.9 s, `assign` 2.4 s and 2.1 s (round 22's 14.2 s with 12.8 s was
the same case on a bigger file) — against **9.2 s and 2 commits**, `commit` 8.3 s, `meta` 2.1 s, `assign` **0.0 s**. A
run whose graph did move still writes and still pays: four changed files with two new declarations ran `communities`
2.3 s, `assign` 2.1 s and `commits` 4. A refresh that changes nothing is still **0.5–0.7 s** with `commit` 0. The suite
is **1 520 green at 0 warnings** — `CommunityAssignerTests` at 7, with two new cases: a document change costs two commits
and leaves the columns alone, and a hand-written `c99` that no community owns is taken back through a document change for
three.

**Left open, with the arithmetic attached**: the write batch's 6.7 s and `meta`'s 2.1 s, and the run's **2 commits** —
1 would mean giving up the per-checkpoint failure unit `026` bought, which is an argument to be answered rather than a
number to be moved. Then the 33 % the store grew in four runs, and, unchanged from round 22, `--prune` at **10.1 s** when
it removes a file, SC-002's 5 000-file tree, SC-006's ratio and SC-003's p95. `002` T034 owns them.


Every round shipped with lines left open on purpose. They are recorded in that round's `tasks.md`, which
is where the argument lives; this section is the index, so nothing has to be found by grepping for an
empty checkbox.

| Round | Open | Why it is still open |
|---|---|---|
| 0 | `002` SC-003 — **closed 2026-11-04 by `048-search-latency-harness`** | 20,400 synthetic passages, 100 `search_code` calls, **p95 50.8 ms** against 250 ms; the first run measured 613.1 ms and exposed the engine `FullTextIndex` cost, replaced by `LexicalIndex` BM25 over the stored tokens. The harness is skipped by default (`SHARPCOREAGENT_RUN_LATENCY=1`), so CI does not pay the build. T034 remains closed by the measured restatement in `research.md` D30 |
| 1 | `007` T031–T032 | an indexed attachment and persisted attachments — T031 is still a specification of its own; T032 is refused on principle. T030 (writable attachment) is closed by `051-writable-attachments`: `--add-root` is writable under `--approval allow`, read-only under `ask`/`readonly` |
| 1 | `003` T032–T034 | `--resume`'s listing is closed by `041-session-resume`; Git-based checkpoints remain out of scope because `035` keeps Git read-only and `036` chose a process-local journal; T034's context-fraction automatic compaction is closed by `042-context-fraction-compaction` |
| 2 | `008` — **closed** | T029 JavaScript/cookies/logins (`052` browser + `057` explicit cookies), T031 paging (`055`) and T033 `robots.txt` (`057`) are closed |
| 3 | `009` T034–T037 | resources, prompts and sampling; remote transports; server mode/reconnection/supervision; a declarative server list (refused). MCP logging notifications are closed by `046-mcp-log-notifications` |
| 4 | `010` — **closed** | T031 browser automation (`052`), T032 PDF/audio/video (`054`), T033 retention (`049`), T034 catalogue (`053`) and T035 image visibility (`017`) are all closed. The remaining `010` work is only the explicitly refused items named in its spec |
| 6 | `013` SC-005 — **closed 2026-11-02 by round 16** (`37c5efa`) | the served call cost 3 199 ms at p95 against a 300 ms budget, restated twice as a miss with the mechanism beside it. It now costs **248–261 ms** over two runs, parity 12/12, with the cold first call published at 2 641–2 834 ms instead of averaged into the p95. What closed it: one loaded build and one computed status per index database, shared across the per-call handles the lease opens and releases — the snapshot was 3 917 ms cold, the status 1 347–1 435 ms per call, and both were instance fields the lease threw away. The row that remains open in this subject is the one above it: the index size that makes the load expensive at all (`002` T034, `research.md` D17–D19), not the lease |
| 9 | `017` T024–T033 | OCR and local vision, PDF/audio/video, pasted or piped images, images in session files, transcript trimming, an image token estimate — T024/T025 by `019`, T031's budget/dedup clauses by `020`/`021`, **T031 transcript trimming by `042-context-fraction-compaction`**, `--image`/stdin by `047`/`050`, **T027's SVG clause by the `SvgImage` amendment**, **T028's resize/rotate clauses by the opt-in `max_dimension` and `rotation`**, and **T033 by serving `view_image` over `mcp serve`**; the rest remains open |
| 15 | `024` — partially closed | `group`/`problemMatcher`/`type` and the custom-agent frontmatter are closed by `056-ide-formats`; `--force`, prompt/instructions/skill files and profile `mcp-servers` remain deliberately refused |

`web_search` is the one capability this program decided *against*, on 2026-10-31, and the reasoning is in the
decision record above. The routing answer is `--mcp`: an official search server, or any search server the
developer already trusts, arrives under its own tool names with its key in their own environment —
`README.md` carries the command lines. What that costs is named rather than hidden: a search server is a
program the developer installs and a key the developer keeps. `018`, released with the decision, stays
unassigned until a round needs it. `012` is the compiler oracle, which needs neither a provider nor a key,
`015` is F# code intelligence, `016` is model roles, `017` is the image view, `019` is image ingress, `020`
is the picture budget of one turn and `021` is the picture the conversation already carries.

## What stays out of this program

Checkpoints and rewind, rules files (`AGENTS.md`, `.clinerules`), remote or team sessions, a plugin
marketplace. Each is a real capability and each is a separate program; naming them here is what keeps this
one finishable.

MCP *server* mode used to be on that list. It moved to round 6 (accepted 2026-10-28) for one reason and
with one limit: what the server publishes is the index's evidence, not the machine. If a later round wants it
to publish a tool that writes, starts a process or reaches the network, that is not an extension of round 6 —
it is a different program, and it goes back on this list.

