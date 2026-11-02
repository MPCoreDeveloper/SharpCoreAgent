# Spec — 022 ide client config

**Status**: implemented in this working tree, in one slice, with no merge commit — the shape rounds 6 and 9–12 took.
Spec number `022`: `018` was released by the round-8 decision record and is not taken here, and `022` is the next
number after round 12. The Visual Studio 2026 evidence this round rests on was gathered from Microsoft's published
MCP-server documentation on 2026-11-02 and is quoted in `plan.md` under "The facts this round is built on".

**Depends on**: `013` (`mcp serve`, `mcp config --client` and the three blocks that command prints) and nothing else.
No new domain type, no new tool, no new setting.

## Why this round exists

SharpCoreAgent's premise, in this repository's own words, is that an agent which is not this one gets this
repository's index for a command line. Round 6 made that true for three clients, and all three of them are terminal
agents: Claude Code reads `mcpServers` from `.mcp.json`, VS Code reads `servers` from `.vscode/mcp.json`, Codex reads a
table from `config.toml`. The fourth client is the one that is not a terminal at all — Visual Studio 2026, whose agent
reads `.mcp.json` from the solution folder, under `servers`, with an explicit `"type": "stdio"`. That is a *file* and a
*key* that no command in this build names.

The consequence is quiet rather than loud. `--client vscode` prints the right key for the wrong file, and its note
says "workspace scope: VS Code reads .vscode/mcp.json" — a true sentence about a different product. A developer who
follows the README pastes it and finds the server in VS Code's list and nowhere in the IDE's, which is exactly the
failure a snippet command exists to prevent.

The second gap is the file. `mcp config` prints, and the README teaches a redirect. A redirect truncates. `.mcp.json`
is the one file two clients read under two different keys — `mcpServers` for Claude Code, `servers` for Visual Studio —
so the documented workflow destroys the first client's key when the second is added. A repository that wants both
agents on one committed file cannot have both, and nothing in the output says so.

## What it adds

| Surface | Layer | Shape |
|---|---|---|
| `McpClientKind.VisualStudio` | `Cli` | the fourth client: `.mcp.json` in `--workspace`, `servers`, typed; `vs` accepted |
| `mcp config --write` | `Cli` | merge the block into that file instead of printing it; stdout stays empty |
| `McpConfigWriter` | `Cli` | the merge itself, three outcomes (`created`, `updated`, unchanged) and one refusal |
| `McpClientSnippets.TryShape` / `Entry` | `Cli` | the one place that says which key a client reads and whether it wants a `type` — shared by the printer and the writer |
| exit `7` on a merge that cannot happen | `Cli` | the client's own file, refused with the file untouched |

## Requirements

- **FR-001** `mcp config --client visualstudio` prints the block Visual Studio 2026 reads — a `servers` object holding a
  `sharpcoreagent` entry with `"type": "stdio"`, this binary's absolute path and the `mcp serve` arguments — names
  `.mcp.json` in `--workspace` on stderr, and exits 0. `vs` is accepted as a spelling, and both names are matched
  case-insensitively like the other three.
- **FR-002** `visualstudio` and `claude` name the same file and different keys, and both notes say so. One committed
  `.mcp.json` serving both agents is the target state of this round, not an accident of two commands.
- **FR-003** `--write` merges the block into the file the client reads instead of writing it to stdout, and writes
  nothing at all to stdout. The merge is three-level: the document keeps every top-level key it had, the client's object
  keeps every other server, and this entry keeps every field this command does not own — an `env` block, a `cwd`.
- **FR-004** `--write` reports one line on stderr naming the file and one of three outcomes: created, updated, or
  unchanged. Unchanged means the file was not written to at all — byte for byte — and it exits 0. A verb that fails
  because the work was already done is a verb that breaks the script that reruns it.
- **FR-005** A file that cannot be merged is refused with the file exactly as it was and exit `7`: not valid JSON, a
  JSON value that is not an object, or a `servers`/`mcpServers` key holding something that is not an object. The exit
  code is `7` rather than `2` because the command line was understood and the thing to fix is the file — a script that
  retried a `2` would retry a typo forever.
- **FR-006** `--write` is refused at parse time on every other command and on the other two `mcp` verbs, and for
  `--client codex`, whose file is TOML. Exit `2`, with nothing opened and nothing read. `mcp serve` is the important
  one: its stdout is the protocol, and a mode that wrote a file instead would break the session before the first
  answer.
- **FR-007** No new setting, no environment variable, no new file, and no change to what any existing command prints.
  `--write` writes the file a client already reads; without it, this round's only difference is a fourth name for
  `--client`.

## What it does not do

- **Not a VS Code migration.** `--client vscode` keeps `.vscode/mcp.json`. A build of VS Code that reads a
  workspace-root `.mcp.json` reads the block `--client claude` already prints — same file, same key — and this round
  neither asserts which build prefers which nor changes the file `--client vscode` names. Changing it would invalidate
  every note, script and README line that has landed since round 6, and nothing this round read states that
  `.vscode/mcp.json` has stopped working.
- **Not an agent definition.** `.github/agents/*.agent.md`, the `tools:` list inside one, and `.vscode/tasks.json` are
  the surface that makes SharpAgent *appear* in an IDE's agent picker. This round only makes the server reachable; an
  agent file that names it is a separate slice with its own file format to get right.
- **Not a TOML writer.** `--client codex --write` is refused. A merge into `config.toml` would have to keep tables and
  comments this repository has no reader for, and a hand-rolled rewriter that gets it wrong corrupts a file the
  developer maintains by hand. The printed block plus `>>` is still the Codex route.
- **Not a rewrite of somebody's file.** Only the key this command owns is touched. A file that already matches is not
  written to at all, so its formatting survives; a file that is changed is re-serialised with two-space indentation,
  which is the price of adding a key to a document with no syntax tree.
- **Not atomic by rename.** The entry is one key in a file whose loss costs a re-run of a command with no side effect,
  and a temporary file beside a developer's `.mcp.json` is a file their IDE may try to read. The file is written once,
  after the whole merge succeeded.
- **Not a doctor.** It cannot say whether the IDE accepted the file, whether the server started, or which tool names
  the agent will see. It says what it wrote, and where.
- **Not a change to `mcp serve`.** The published set is still the five read-only evidence tools, and a delegated
  `run` over MCP remains a constitution amendment and a later round.

## Evidence

Twenty-five new cases in three classes, each of them failing without the change:

- `McpConfigWriteTests` (nine cases): the file created, with a note saying so and nothing on stdout; every other
  top-level key and every other server kept; one `.mcp.json` carrying both `mcpServers` and `servers` after two runs;
  a second run leaving the bytes identical and saying nothing was written; an `env` block kept while a stale `command`
  is replaced; `.vscode` created when it is missing; a file that is not JSON and a `servers` key that is an array, both
  refused at exit `7` with the file unchanged.
- `McpConfigCommandTests` (+1): `--client visualstudio` — the key, the explicit `type`, the binary that exists, and a
  note that names `.mcp.json` and says "solution scope"; the existing note case now covers four clients.
- `McpConfigParsingTests` (+15): `visualstudio`, `VS`, `vs` and `VisualStudio` parse to one kind; `--write` parses
  beside `--client` and defaults off; the two hints name four clients; `--client codex --write` is refused; `--write`
  is refused on run, chat, index, models, config, setup, `mcp serve` and `mcp bench`; `--help` and `--version` answer
  with it in the history.
- `HelpTextTests` (no new case): the golden `Serve` section and the `--client` column test were amended for the fourth
  name and the `--write` paragraph, which is what makes a drifted help text fail rather than age quietly.

`dotnet build -c Release` at 0 warnings, 0 errors; `dotnet test -c Release --no-build` at 1,356 passed, 0 failed. That
total includes the twenty-two cases of an unrelated index-timing slice uncommitted in the same tree; the twenty-five
this round adds sit on top of the 1,331 that tree started from, so the round's own delta is 25.
