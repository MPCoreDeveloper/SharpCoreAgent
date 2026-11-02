# Plan — 022 ide client config

The spec is the argument. This is the shape it took, the facts it rests on, the choices that were weighed, and what the
build produced.

## The shape, in one paragraph

`McpClientKind` gains a fourth member, `VisualStudio`, which shares `.mcp.json` with `Claude` and shares `servers`-plus-
`type` with `VsCode`; `visualstudio` and `vs` both parse to it. `McpClientSnippets` grows the two pieces the printer and
the writer must agree about — `TryShape` (which key, and whether a `type` is wanted) and `Entry` (the one object both
file under `sharpcoreagent`) — and keeps `Render`, `Destination` and `Note` for the printing path. `McpConfigWriter` is
the new half: it reads the destination if it exists, merges at three levels, writes once, and answers with one of three
outcomes. `mcp config --write` routes to it and puts nothing on stdout; anything it cannot merge becomes
`McpConfigWriteException` and exit `7`, with the file untouched. `CliOptions` gains `McpConfigWrite`, one `--write` case,
and `RejectWriteFlag` beside `RejectClientFlag`; `FinishMcp` refuses `--client codex --write` before a workspace is
opened.

## The facts this round is built on

Read on 2026-11-02 from Microsoft's published MCP-server documentation for Visual Studio, and quoted here because the
whole round turns on them:

- A `.mcp.json` at the solution folder carries its servers under **`servers`** — not `mcpServers` — and a stdio entry
  says `"type": "stdio"` beside `command` and `args`.
- The documented discovery order is `%USERPROFILE%\.mcp.json`, then `<SLN>\.vs\mcp.json`, then `<SLN>\.mcp.json`, then
  `<SLN>\.vscode\mcp.json`, then `<SLN>\.cursor\mcp.json`.

Two consequences the design answers rather than mentions. The first: `--client vscode`'s **key** is right for Visual
Studio and its **file** is the fourth resort in that order, so a repository that commits the `.vscode` copy is
depending on a fallback — and one developer's `.vs\mcp.json`, which is git-ignored, silently outranks both. The second:
`.mcp.json` is read by two clients under two keys, so the file this round points at is the one file a redirect must not
be used on.

What this round did **not** verify, and therefore does not claim: whether a current VS Code build reads a workspace-root
`.mcp.json` under `mcpServers` (the page moved and did not answer on 2026-11-02), and whether an MCP tool name may appear
in an agent file's `tools:` array. Neither is needed here — `--client vscode` keeps its file, and agent files are the
next slice — and both are written down so the next slice does not read this one as evidence.

## Decisions

**D1 — a fourth client, not a second command.** `--client` already means "whose configuration", so the axis existed and
`visualstudio` joins the other three spellings rather than becoming `mcp config --visual-studio`. `vs` is accepted
because the product's own name is two letters shorter, and the parser already matches case-insensitively.

**D2 — the same file for two clients, deliberately.** `Destination(VisualStudio)` returns `.mcp.json` in `--workspace`,
which is Claude Code's file. Giving Visual Studio the `.vscode` copy would have looked tidier and would have been the
wrong advice: it is the last resort of the documented search, and a git-ignored `.vs\mcp.json` outranks it. One
committed `.mcp.json` beside the `.sln` is the file both clients read and the team shares.

**D3 — a redirect is not the documented route any more, so `--write` merges.** The alternatives weighed: (a) a
`--client both` that prints a document carrying both keys — rejected, because that is a combination rather than a
client, and it would need a shape of its own for every future pair; (b) writing into the file always and printing never
— rejected, because printing is what a terminal, a diff and a chat window can consume, and round 6 pinned stdout as the
block; (c) merging only when the file exists — rejected, since "there is no `.mcp.json` yet" is the common case.
So printing stays the default and `--write` is a second mode of the same command, with stdout empty: a caller piping
`mcp config --write` into a file would otherwise append a second copy to the one just merged.

**D4 — three outcomes, and "unchanged" writes nothing at all.** Created, updated and unchanged, all exit 0, one line
each on stderr. A verb that failed because the work was already done would break the script that reruns it, and a verb
that rewrote an identical document would reformat a file for no reason. The comparison is between the entry as it was
serialised and the entry after the merge, so "unchanged" is a claim about what the client will read, not a guess from
timestamps.

**D5 — the merge keeps what the command does not own.** Three levels: the document keeps every top-level key (a
`$schema`, another client's object), the client's object keeps every other server, and the `sharpcoreagent` entry keeps
every field the command does not set — an `env` block, a `cwd`. `type`, `command` and `args` are the parts that describe
*this binary* and the parts that go stale when it moves, so those are the ones overwritten. A merge that also dropped
`env` would make the flag a trap for the one developer who needed it most.

**D6 — refuse at parse time what cannot work.** `--write` on run, chat, index, models, config, setup, `mcp serve` and
`mcp bench` is a usage error (`2`) and nothing is opened; `--client codex --write` is the same, because `config.toml`
holds whichever tables and comments the developer already has and this repository has no TOML reader. `mcp serve` is the
one that matters most: its stdout is the protocol, and a mode that wrote a file instead would break the session before
the first answer. The reasoning is `RejectClientFlag`'s, one flag over: a command told to write a file and not writing
one sends the developer looking for it.

**D7 — a file that cannot be merged exits `7`, not `2`.** The command line was understood; the thing to fix is the
file, and a script that retried a `2` would retry a typo forever. Three cases: not valid JSON, a JSON value that is not
an object, and a `servers`/`mcpServers` key holding something else. In all three the file is untouched, because a
refusal about a file is only useful if the developer who opens it sees what the command saw.

**D8 — one shape helper, shared.** `TryShape` is the only place that says which key a client reads and whether it wants
a `type`; `Entry` is the only place the object is built. A printer that said `mcpServers` beside a writer that used
`servers` would produce a file with a second, ignored entry after every run, and no test of either half alone would
notice.

**D9 — no setting.** `--write` is not a preference: one developer's habit is not another's, and the flag changes a file
rather than answering a question. It has no environment variable and no settings key, and a `config show` after this
round lists exactly what it listed before.

**D10 — not atomic by rename.** The obvious belt is a temporary file plus `File.Move`. It buys little here: the entry is
one key in a file whose loss costs a re-run of a command with no side effect, and a temporary `.mcp.json.tmp` beside a
developer's file is a file their IDE may try to read as a server list. The document is built completely in memory and
written in one `WriteAllBytes`, which is the failure mode that matters — never half a document.

## What the build produced

`dotnet build -c Release` at 0 warnings, 0 errors. Two warnings this round raised and then removed rather than
suppressed: Sonar `S3871` wanted `McpConfigWriteException` public, and it is (like `CliUsageException`); the test
analysers wanted `File.WriteAllTextAsync` and a `TestContext.Current.CancellationToken`, and the tests use both.

`dotnet test -c Release --no-build` at 1,356 passed, 0 failed — twenty-five of them this round's, on a tree that already
carried twenty-two from an unrelated index-timing slice. The five help-text cases that pin the block needed amending
rather than adding: the `--client` row, the golden `Serve` section, and two `Contains` assertions, which is the cost of
documenting a flag in the one place a developer reads before running anything.

## Complexity Tracking

No violation to record. The round adds one command-line flag, one enum member, one static class in `Cli`, and no
dependency; `Process.Start` is not involved, no tool was added, and the domain project is untouched. The one piece of
new machinery — a JSON merge — uses `System.Text.Json.Nodes`, which the project already references for the wire format.

## What was deliberately not built

- **A `--client both`.** A combination is not a client, and the merge already gives the same file both keys with two
  commands that are each true on their own.
- **A TOML merge.** `--client codex --write` is refused rather than approximated; a rewriter that loses a comment is
  worse than a refusal that names the reason.
- **`.vscode/tasks.json` and `.github/agents/*.agent.md`.** The next slice: this round makes the *server* reachable from
  the IDE, and that one makes *the agent* appear in its picker.
- **A `mcp config --check`.** Reading the file back and saying whether it names this binary is a two-line feature with a
  real question underneath it — what to do when another copy of the binary is named — and the answer belongs with
  whichever round implements `--patch` and IDE review, not here.
- **Any change to `mcp serve`.** Its published set is unchanged and it still refuses `--write`, which one case pins.
