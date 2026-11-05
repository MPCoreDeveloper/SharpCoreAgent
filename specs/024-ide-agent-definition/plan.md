# Plan — 024 ide agent definition

**Status**: implemented in this working tree, in one slice, with no merge commit. The argument is `spec.md`; this file
is the evidence, the decisions, and the two things the round could not verify.

## The facts this round is built on

Both formats are somebody else's, so neither is inferred from an example in the wild. They were read on 2026-11-02 from
GitHub's Copilot reference (`docs.github.com/copilot/reference/custom-agents-configuration`, and the two `data/reusables`
partials the how-to page includes) and from VS Code's tasks page (`code.visualstudio.com/docs/debugtest/tasks`).

**The profile file.** From the how-to page: a new agent "will open a template agent profile called `my-agent.agent.md` in
the `.github/agents` directory of your target repository", and "the filename may only contain the following characters:
`.`, `-`, `_`, `a-z`, `A-Z`, `0-9`". From the configuration partial: "write a `name` … If unset, the name will default to
the filename (without the `.md` or `.agent.md` suffix)", "write a brief `description` (required)", and for the prompt,
"the prompt can be a maximum of 30,000 characters".

**The tool list.** The same partial gives the syntax verbatim, MCP server included:

> In the `tools` property, define which tools the agent can use. This is a list of tool names or aliases, including tools
> from MCP servers configured in the repository settings or the agent profile (for example,
> `tools: ["read", "edit", "search", "some-mcp-server/tool-1"]`). If you omit this property, the agent will have access
> to all available tools.

So an MCP tool is `<server>/<tool>`, and the prefix is the server's name — which for this build is `sharpcoreagent`, the
key `mcp config --client vscode` already writes and the name the server answers in `serverInfo`.

**The keys this round does not write.** From the frontmatter table: `name` (optional), `description` (**required**),
`target` ("`vscode` or `github-copilot`. If unset, defaults to both environments"), `tools`, `model` ("If unset, inherits
the default model"), and `mcp-servers` — marked "**Not used in VS Code** and other IDE custom agents".

**The task document.** VS Code's tasks page, verbatim: workspace tasks "are configured from the `tasks.json` file in the
`.vscode` folder for a workspace", and its handcrafted example is `{ "version": "2.0.0", "tasks": [ { "label": "watch",
"command": "tsc", "args": ["--watch"], … } ] }`. Those five keys — `version`, `tasks`, `label`, `command`, `args` — are
the ones this round emits, and nothing else (see D7).

## Decisions

**D1 — a command, not a file this repository commits and calls a feature.** The deliverable is `ide agent --write`
writing *a developer's* workspace. This repository runs it against itself afterwards (see "What the build produced"),
which is how the round gets an end-to-end check without hard-coding one machine's path into a template.

**D2 — a new top-level `ide`, rather than two more `mcp` actions.** `mcp config` is about a *server*: four clients, three
files, the key each one reads. What this round writes is about the *editor*: an agent profile the editor's picker lists,
and a task the editor's task list runs. The task invokes `sharpcoreagent index`, which has nothing to do with MCP at all,
and filing it under `mcp` would be filing it by the wrong noun. `ide agent` and `ide tasks` are also the two words a
developer would guess, which is the whole test a command name has to pass.

**D3 — the tool list is derived, not copied.** `AgentProfile` builds its `tools` line from
`McpServerCatalog.PublishedToolNames` — the array `McpServerPublishedSet` filters by — so the profile and the server
cannot disagree. That is `022`'s D8 one step further out, and the drift it prevents is silent in the worst direction: a
profile naming a tool the server no longer publishes, which the editor reports as a missing tool at the moment a
developer asks a question. `McpServerCatalog` is the one new public type in `Infrastructure`; the names are a contract
(`contracts/` documents them) and the CLI is a legitimate second reader. Enforcement stays where it was — a name in the
catalogue that the registry does not hold, or that is no longer read-only, is still served to nobody.

**D4 — the profile is replaced whole, and only when it is ours.** A `.mcp.json` merge works because JSON has slots: a
key, a section, an entry, and every level keeps what it does not own. A profile has none — YAML frontmatter plus prose,
with comments and ordering the format does not fix — so a "merge" would be a rewriter, and a rewriter that loses a
sentence is worse than a refusal. The file therefore carries a marker on its last line, `--write` replaces the file only
when that marker is present, and a file without it is refused at exit `7`, untouched, with both ways forward named.
A developer who deletes the marker has claimed the file, which is the correct reading.

**D5 — `--write` is the flag `022` added, not a second flag.** The spelling is unchanged; the property it sets is renamed
from `McpConfigWrite` to `Write`, because two commands now read it and a name that says one of them would be a name that
lies. The refusal was widened rather than duplicated: `--write` on anything but `mcp config` and `ide` still throws, with
both example commands in the hint.

**D6 — `tasks.json` is merged by label.** Appending would leave a second `sharpcoreagent: index` after the second run,
and a task list with two identical labels is a list where the developer cannot tell which one runs. So the document is
parsed, the task with our label is replaced in place (or appended when there is none), `version` is kept when the file
has one and written when it does not, and every other key — `tasks`, `windows`, `options`, somebody else's tasks —
survives. The serialised text is then compared with what was read, so a file that already says this is not rewritten.

**D7 — emit only the keys the documentation shows.** `version`, `tasks`, `label`, `command`, `args`. The tasks page
describes `group`, `problemMatcher` and `type` only in prose this round did not read to the end, and a guessed
`problemMatcher` value is a file VS Code rejects — worse than a task with no matcher, which is a shell task that runs.
The cost is named in `spec.md`: this task is not Ctrl+Shift+B's default, because that is `group`'s job.

**D8 — the task names this binary's absolute path.** `McpClientSnippets.Invocation` already answers "how must this binary
be started", including the `dotnet <assembly>` case a debugger or a test host produces; the round generalises it from
`Invocation()` to `Invocation("mcp", "serve")` and `Invocation("index")` so one helper answers both. The honest cost: a
committed `.vscode/tasks.json` then carries one machine's path, exactly as `mcp config --write` already commits one into
`.mcp.json`. The alternative — bare `sharpcoreagent`, resolved from `PATH` — is a task that fails for anyone who installed
it somewhere unusual, and fails with "command not found", which is the FAQ entry at the bottom of the page this round read.

**D9 — exit `7` for a file, exit `2` for a command line.** The split `022` chose, unchanged, because the person who fixes
them is different: `2` means the developer typed something, `7` means a file in the tree needs a human. `ide` adds no exit
code of its own.

**D10 — nothing is opened.** No index, no settings document, no container, no credential, no socket, no process. The
command answers a question about files, and a developer wiring up an editor should not need an index to exist first. That
is also why `--no-index` is refused rather than ignored: there is nothing for it to turn off.

**D11 — this repository commits the profile and not the task.** Running both actions against this tree is the round's
end-to-end check (D1), and the two results differ in portability: the profile is the same bytes on every machine, while
the task's `command` is one machine's absolute path — D8's named cost, materialised. So `.github/agents/sharpcoreagent.agent.md`
is committed as the worked example, and `.vscode/tasks.json` was written, read and deleted. A developer who wants the task
runs one command and gets their own path.

## What the build produced

`dotnet build -c Release` at 0 warnings, 0 errors. `dotnet test -c Release --no-build` at 1,472 passed, 0 failed, 0
skipped — fifty-four more than the 1,418 the tree started from, which is the whole round's delta: `AgentProfileTests` 13,
`IdeCommandParsingTests` 25, `IdeTasksWriteTests` 8, `IdeAgentWriteTests` 6, and two added to `HelpTextTests`. Two existing
cases were amended instead of added, both because a message this round changed on purpose: the `--write` refusal in
`McpConfigParsingTests` now names both commands, and the usage block's `0` row now covers a written file.

Then the real binary, against this repository, which is the check the artefact can make and a test cannot:

```text
sharpcoreagent ide agent --write   → created .github\agents\sharpcoreagent.agent.md        exit 0
sharpcoreagent ide tasks --write   → created .vscode\tasks.json (sharpcoreagent: index)    exit 0
sharpcoreagent ide agent --write   → already says this; nothing written                    exit 0
sharpcoreagent ide tasks --write   → already carries the task; nothing written             exit 0
sharpcoreagent ide                 → error: ide needs an action.                           exit 2
sharpcoreagent ide bogus           → error: 'bogus' is not an ide action.                  exit 2
sharpcoreagent ide agent --json    → error: --json is only valid with run, index and ui.    exit 2
sharpcoreagent run hello --write   → error: --write applies to mcp config and ide.          exit 2
```

The profile the binary wrote is the one committed beside this file; the task list is the one D11 explains.

## Complexity Tracking

No violation to record. The round adds one command with two actions, three static classes in `Cli` (a profile builder, a
task builder, and the writer both share), one public read-only array in `Infrastructure`, one renamed option property and
one generalised helper signature. No dependency, no `Process.Start`, no tool, no setting, no domain type, and the index is
untouched.

## What was deliberately not built

`spec.md` carries the list with its reasons. The two worth repeating: no VS Code extension (a second toolchain, and
`023`'s own next round), and no `--force` (a flag whose only purpose is to destroy somebody's edits).

## What was not verified

- **That VS Code resolves a `<server>/<tool>` entry against the server names in `.vscode/mcp.json`.** The syntax is
  documented, the prefix is documented as an MCP server's name, and both files this build writes name the same server —
  but nobody here opened the agents dropdown in VS Code and saw the six tools listed. What a test can pin is narrower,
  and was pinned: the profile's prefix is the same constant `mcp config` writes.
- **That a profile without `target` appears in both environments.** The table says "If unset, defaults to both
  environments"; this round takes it at its word and sets nothing.
