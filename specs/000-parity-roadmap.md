# Roadmap — the capability rounds

**Status**: round 0 landed (constitution 1.1.0), round 1 specified, rounds 2–5 planned
**Supersedes**: nothing. `001`–`006` stay the record of what was built and why.

## Why rounds rather than one specification

`001`–`006` each shipped one capability behind one reviewable change. This program adds what a
developer arriving from Cline, Codex CLI or Claude CLI reaches for on the first day, and each piece
is independently shippable, independently testable and independently reviewable. As one branch it
would be a month of work nobody can hold in their head; as six it is six merges, each of which can be
reverted on its own. The constitution was amended once, at the front (1.0.2 → 1.1.0), so no round has
to negotiate permission for its own existence.

## What "parity" means here

The four gaps a newcomer hits, in the order they hit them.

1. *"That file is in another repository."* — a turn can see exactly one tree; attaching a second one
   is impossible without moving the first.
2. *"Read this page and tell me what it says."* — there is no way to fetch a URL at all.
3. *"Use the browser, GitHub, Postgres."* — there is no extension mechanism, so every integration is a
   new project in this repository.
4. *"Do this in a sub-agent, keep my context clean."* — `task` was drafted
   (`TaskTool.cs.pending`) and never finished.

## The rounds

| Round | Spec | Ships | Depends on |
|---|---|---|---|
| 0 | `.specify/memory/constitution.md` **1.1.0** | permission for rounds 1–4, and nothing else | — |
| 1 | [`007-external-roots`](007-external-roots/spec.md) | read another project; never write it | 0 |
| 2 | `008-web-fetch` | `fetch_url` — a page as bounded, readable text | 0 |
| 3 | `009-mcp-client` | `--mcp <command>` — any stdio MCP server's tools | 0 |
| 4 | `010-browser` | a browser, through an MCP server | 3 |
| 5 | `011-subagent-task` | `task` — delegation with an isolated transcript | — |

`web_search` is deliberately unnumbered: it needs a provider and a key, which is a decision this
repository has not made. If it is made, it becomes `012` and it does not change 007–011.

## Decision record

- **The browser is not a binary we ship.** A native headless browser would break the "no native
  binaries, no platform-specific code" constraint and add a 100–300 MB download to a tool that
  currently ships as assemblies. Round 3 gives the extension seam; round 4 uses it with the Playwright
  or Puppeteer MCP server the developer already has, which is exactly how Cline, Codex CLI and Claude
  Code get browser control. If in-box automation is ever wanted, it is a new amendment and a new
  round, not a change to round 4.
- **The network stays off until asked for.** `--web` and `--mcp` are per-process opt-ins. A build that
  configures nothing makes the same requests it makes today.
- **Attached roots are read-only and per invocation.** Not persisted, not writable, not indexed.
  Rationale in `007-external-roots/spec.md`; the short version is that a directory the agent may read
  is context for one task, and a persisted list is authority nobody remembers granting.
- **Delegation is not a new process.** `task` runs the same orchestrator in-process with an isolated
  transcript, which keeps one budget, one approval mode and one transcript format.

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
   the suite count in `README.md` corrected to the number the run printed.
7. The round's `spec.md` metadata switched from `specified` to `implemented`, naming the branch it
   actually merged from.

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

## Round 1 — `007-external-roots`

**Ships**: `--add-root <path>` (repeatable) and `SHARPCOREAGENT_ADD_ROOTS`; a read path through
`Workspace`/`PathSafety` that accepts an absolute path inside any attached root; `read_file`,
`list_directory` and `search_text` honouring it; every write, delete and shell call still confined to
the primary root; a `list_roots` tool; a banner line and `/roots`.

**Does not ship**: writes into an attachment, a second index, persisted attachments, per-root
approval.

**Evidence**: a turn reads a file in a second project and cannot write it; a link inside an attachment
that points outside every root is refused; no existing assertion about write confinement is deleted to
make the round pass.

## Round 2 — `008-web-fetch`

**Ships**: one read-only tool, `fetch_url`, behind `--web`. It fetches `http`/`https` only, with a
timeout, a byte ceiling, a redirect ceiling and a character ceiling; it refuses loopback, link-local,
private and cloud-metadata addresses unless the developer names them; it sends no credential and no
cookie. For HTML it returns a readable reduction — entities decoded, `script`/`style` removed, tags
stripped, whitespace collapsed — implemented in the BCL, not with a new dependency. The result names
the final URL after redirects, the status, the content type, and whether the text was truncated.

**Does not ship**: JavaScript execution, cookies, logins, screenshots, a search engine.

**Evidence**: a deterministic `HttpMessageHandler` stub replaying recorded responses, in the shape the
provider adapters are already tested with — no live network in the suite.

## Round 3 — `009-mcp-client`

**Ships**: `--mcp <command>` (repeatable), a stdio JSON-RPC client (`initialize`, `tools/list`,
`tools/call`, notifications), and an adapter that turns each MCP tool into an `ITool` in the same
`IToolRegistry` the built-in tools use. `readOnlyHint` maps to `ToolSafety.ReadOnly`; anything else is
`Destructive` and prompts. Results are bounded by `MaxToolResultChars`; the server's stderr goes to
`--verbose` and never into the transcript. The child is killed when the session ends, the way
`ShellProcessRegistry` kills a shell command.

This closes `T040` in `specs/003-terminal-console-agent/tasks.md`, which has been open since 003.

## Round 4 — `010-browser`

**Ships**: documentation and a thin `browser` tool that speaks to an MCP server the developer already
installed (Playwright MCP first), plus the transcript rendering for the screenshot and DOM artefacts it
returns. If the server is absent the tool is absent — there is no fallback that pretends to browse, and
no first-run that downloads one.

**Does not ship**: a bundled browser, a driver manager, a JavaScript engine of our own.

## Round 5 — `011-subagent-task`

**Ships**: `task` promoted from `TaskTool.cs.pending` to a registered tool. A sub-agent gets the same
model, a child registry without `task` (no recursion), `SubAgentPrompt` as its system prompt, and an
approval mode that is `AllowAll` only when the parent is — otherwise `ReadOnly`, because a child that
can silently write is a parent that can silently write. The child's transcript is discarded; its final
message is the tool result, bounded like any other.

**Decision recorded now**: the draft is kept as this round's design and is deliberately not compiled.
It moves into `specs/011-subagent-task/` when that spec is written, it does not ship before it, and it
is not deleted, because the prompt and the child-registry argument in it are the round's starting point
rather than a guess.

## What stays out of this program

Checkpoints and rewind, rules files (`AGENTS.md`, `.clinerules`), MCP *server* mode, remote or team
sessions, a plugin marketplace. Each is a real capability and each is a separate program; naming them
here is what keeps this one finishable.

