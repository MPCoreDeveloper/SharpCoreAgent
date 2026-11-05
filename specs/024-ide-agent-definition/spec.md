# Spec — 024 ide agent definition

**Status**: implemented in this working tree, in one slice, with no merge commit — the shape rounds 6 and 9–14 took.
Spec number `024`: `023` is the last round, and `018` is still released by the round-8 decision record, so it is not
taken here. The two document formats this round writes were read from Microsoft's published references on 2026-11-02 and
are quoted in `plan.md` under "The facts this round is built on".

**Depends on**: `013` (the six tools `mcp serve` publishes, which are the profile's whole tool list), `022` (`mcp config`
and the `--write` flag this round widens from one command to two), and nothing else. No new domain type, no new tool, no
new setting, no dependency.

## Why this round exists

Round 6 made this workspace's index reachable from three terminal agents; round 13 added a fourth client, the IDE's own
MCP client; round 22 made that fourth client's file writable; round 23 gave the developer sitting in the editor a page.
All four are about a *process* the editor starts. None of them makes the *agent* appear in the editor's picker — a named
thing a developer selects before typing, carrying a description and a tool list.

That is one more file, `.github/agents/<name>.agent.md`, and this build names it nowhere. The consequence is quiet: a
developer follows `README.md`, gets the server into VS Code's MCP list, opens the agents dropdown, and finds it absent —
because a configured server is a set of tools, and an agent is the thing that decides to use them. `022`'s plan recorded
this as the next slice and stopped there ("this round makes the *server* reachable from the IDE, and that one makes *the
agent* appear in its picker").

The second gap is smaller and is the same gap `022` closed for `.mcp.json`: a workspace that wants a fresh index one
keystroke away has to write `.vscode/tasks.json` by hand, and the command line it needs — this binary's absolute path,
then `index` — is one `mcp config` already knows how to work out and one a developer has to guess.

## What it adds

| Surface | Layer | Shape |
|---|---|---|
| `ide agent` | `Cli` | prints `.github/agents/sharpcoreagent.agent.md`, or `--write` writes it |
| `ide tasks` | `Cli` | prints `.vscode/tasks.json`, or `--write` merges one task into it |
| `AgentProfile` | `Cli` | the profile's text: frontmatter (`description`, `tools`) plus the prompt |
| `WorkspaceTasks` | `Cli` | the one task: `sharpcoreagent: index` |
| `WorkspaceFileWriter` / `WorkspaceWriteException` | `Cli` | the save and the refusal both writers share; the type `022` introduced, widened |
| `McpServerCatalog` | `Infrastructure` | the six published names, made public because the profile lists exactly them |
| exit `7` on a file that cannot be read, merged or written | `Cli` | the same code `022` chose for a client's file |

## Requirements

- **FR-001** `sharpcoreagent ide agent` prints the profile to stdout and one line to stderr naming
  `<workspace>/.github/agents/sharpcoreagent.agent.md`, and exits 0. Nothing else is opened: no index, no settings, no
  container, no credential, no process, no socket.
- **FR-002** The profile begins with YAML frontmatter, closed by a `---` line, whose keys are `description` and `tools`
  and nothing else. `description` is one line. `name` is omitted because the file's name is the name the picker shows;
  `model` because the model a developer's editor runs on is theirs, not this command's; `target` because an unset value
  is documented to mean both environments; `mcp-servers` because the reference says IDE agents do not use it.
- **FR-003** `tools` lists exactly the six names `mcp serve` publishes — `code_context`, `find_references`, `find_symbol`,
  `index_status`, `search_code`, `view_image` — each prefixed with the server's name (`sharpcoreagent/`), in the order the contract
  documents. The list is derived from `McpServerCatalog.PublishedToolNames`, the same array `McpServerPublishedSet`
  filters by, so a seventh tool appears in the profile and the server together or in neither.
- **FR-004** The body names each of the six tools with what it answers, says in one sentence that all six are
  read-only, tells the reader to quote the `path:start-end` a tool returned rather than invent one, and tells it to
  report `missing`, `stale` or `partial` from `index_status` instead of guessing. It is well under the documented
  30,000-character cap on a profile's prompt.
- **FR-005** The profile's last line is a marker naming the command that wrote it. `--write` replaces the file whole only
  when that marker is present, and otherwise refuses at exit `7` with the file untouched and both ways forward named —
  delete it, or move it aside.
- **FR-006** `ide agent --write` writes nothing at all to stdout, creates `.github/agents` when it is missing, leaves an
  identical file untouched, and reports `created`, `updated` or `unchanged` in exactly one line on stderr.
- **FR-007** `sharpcoreagent ide tasks` prints a `.vscode/tasks.json` document: `"version": "2.0.0"` and one task whose
  `label` is `sharpcoreagent: index`, whose `command` is this binary's absolute path, and whose `args` end in `index`.
  It exits 0.
- **FR-008** `ide tasks --write` merges rather than replaces: every other top-level key and every other task survives, a
  task carrying this label is replaced rather than duplicated, `version` is kept when the file has one and written when
  it does not, and a file that already says this is not written to at all.
- **FR-009** A `tasks.json` that is not JSON, or whose `tasks` is present and not an array, exits `7` with the file
  untouched.
- **FR-010** Both actions honour `--workspace` and write only inside it. A path holding spaces or non-ASCII characters
  is written as it is; the JSON is built through `JsonNode`, so a Windows path arrives escaped.
- **FR-011** Each of these is refused at exit `2`: `ide` with no action, an action that is neither `agent` nor `tasks`,
  the flags that name a model or a transcript (`--provider`, `--model`, `--planner`, `--executor`, `--reviewer`,
  `--base-url`, `--fallback`, `--session`), a client (`--client`), the network (`--web`, `--web-allow`, `--mcp`), an
  index action (`--status`, `--rebuild`, `--verify`, `--prune`) and `--no-index`, an attachment (`--add-root`), the
  panel's flags (`--serve`, `--port`, `--refresh`), the refresh switch (`--no-refresh`), and `--json`.
- **FR-012** Neither action reads or writes the index, builds a container, reads a credential, starts a process or
  touches the network. `Process.Start` appears nowhere in this round; the shell tool and the MCP client keep their two
  call sites.

## Edge cases

- **A workspace with no `.github` and no `.vscode`.** Both are created. A repository that has never had one is the
  common case, and the command's whole job is that the next step is not "make the folder first".
- **A profile a person wrote.** No marker, so it is refused rather than replaced: a frontmatter-and-prose file has no
  slots to merge into, and the loss would be their words rather than one key of ours.
- **A `tasks.json` that starts with `//` comments.** Comments are VS Code's own dialect and not JSON, so the file is
  refused at `7` and the message says the task can be added by hand. Reading a comment dialect this build has no reader
  for would be a second parser, not a merge.
- **A second run.** `unchanged`, one line, no write: the bytes are compared before anything is replaced, so a
  formatting difference a developer made by hand is not silently normalised away.
- **A read-only checkout.** Exit `7`, with the OS message and the path, and nothing half written — the document is
  built completely before the file is opened.
- **`--workspace` naming a directory that does not exist.** The write fails at `7` naming the path, rather than
  creating a tree the developer did not name.
- **The profile in a repository that already has one from another tool.** Different file name, so both are read: the
  picker lists two agents, and nothing here pretends otherwise.

## What this round does not build

- **A VS Code extension.** `023` recorded its cost — a TypeScript project, `npm`, a bundler, a `.vsix` — and named it
  as the next round's work against the panel's pinned route. This round is the other half of the same picker, and needs
  no toolchain: a Markdown file and a JSON file.
- **A `--client`.** The profile is not per-client. VS Code, the Copilot CLI and the Copilot cloud agent read the same
  directory, which is why the command names the server and not a client.
- **A prompt file, an instructions file, an agent skill.** `.github/prompts/*.prompt.md`, `.github/copilot-instructions.md`
  and `.github/skills/` are each a different format with a different audience; the profile is the one that puts tools in
  a picker.
- **`mcp-servers` inside the profile.** The reference says IDE agents do not use it, and this build already has the
  command that writes the server's configuration: `mcp config`.
- **`group`, `problemMatcher` and `type` in the task.** The three keys the tasks page describes only in prose are left
  out rather than guessed. A task with `label`, `command` and `args` is a shell task with no matcher, which is what this
  one is; `group: build` would make it Ctrl+Shift+B's default and is a one-line addition for a round that reads that
  part of the page.
- **`--force`.** Overwriting a file this command did not write is not a convenience, and a flag whose only purpose is to
  destroy somebody's edits is one this build does not have.
- **A Visual Studio agent definition.** Visual Studio 2026's agent picker has its own file format and its own location,
  and this round read neither.
- **Anything published over `mcp serve`, any setting, any environment variable.** The published set is still six
  read-only tools, and neither action appears in the settings file or the settings table.

## What makes this round done

- `AgentProfileTests`: the frontmatter opens and closes, carries `description` and `tools` and no key this round decided
  against; the tool list equals the published set, prefixed and in order; every tool is named in the body; the marker is
  the last line; the document is under the documented cap.
- `IdeAgentWriteTests`: created with an empty stdout and a note naming the path; a second run unchanged and not written;
  a file with the marker and other content updated; a file without the marker refused at `7` and left alone.
- `IdeTasksWriteTests`: created; another task and another top-level key survive; our task replaced rather than
  duplicated; unchanged on the second run; a file that is not JSON and one whose `tasks` is an object both refused at
  `7` with the bytes preserved.
- `IdeCommandParsingTests`: `ide` alone, an unknown action, each of FR-011's flags, `--write` defaulting off, and the
  commands `--write` is still refused on.
- `HelpTextTests`: the usage block names both actions, the `--write` row names both commands, and the exit-code table's
  `0` row covers a written file.
- `dotnet build -c Release` at 0 warnings, 0 errors; `dotnet test -c Release --no-build` green, with the round's own
  delta recorded in the commit that carries it.
