# Plan — 016 model roles and the pasted key

## Shape

Two independent halves, which is why they are one round: neither touches the loop.

```text
--planner / settings planner / SHARPCOREAGENT_PLANNER_MODEL
        |                    --executor ...                --reviewer ...
        v                          v                            v
    CliOptions.PlannerModel    .ExecutorModel              .ReviewerModel      (text, not a descriptor)
        |                          |                            |
        +--------------------------+----------------------------+
                                   |
                              AgentRoles.For / ForMode / RequestModels      CLI only
                                   |                    |
              RunCommand / ChatCommand /mode        CliHost preflight
                                   |                    |
                            AgentSession.ActiveModel   CredentialPrompt -> CredentialFile.Upsert
```

## Layers, and what each one is allowed to know

**CLI (`SharpCoreAgent.Cli.AgentRoles`).** `AgentRole` (Planner, Executor, Reviewer) lives here, not in `Domain`.
The model is resolved before a session exists and the mode is already a session concept, so "which model answers
this job" is a host decision; putting it in `Domain` would put a provider-shaped question in the layer principle I
keeps free of providers, and `Application` would have to learn a word it never uses. `AgentRoles` owns the three
names, the labels (`plan`/`act`/`review`), resolution through `ModelCatalog`, and `RequestModels`, which is the one
place the preflight gets its list.

**CLI (`CredentialPrompt`).** A small class with four seams — input, two streams and the terminal probe — plus three
overridable collaborators: the masked reader, the environment reader, and where a pasted key is put for this
process. Every one exists because a test must be able to script a conversation without a terminal, and because the
production path must not print a key. The store itself is untouched: a pasted key is set as an environment variable
for this process, which is a source `EnvironmentCredentialStore` already reads, and `ChildEnvironment` already
strips every `*_API_KEY` from a child process, so a build or a test run started by a tool does not inherit it.

**Infrastructure (`CredentialFile.Upsert`, `DefaultPath`).** The file grammar already lives here, so the writer does
too: it re-reads and re-parses before writing (a file this grammar rejects is never rewritten), replaces the line
that names the variable, keeps everything else verbatim, writes through a `.tmp` sibling, and applies user-only
modes on Unix. `DefaultPath` places `credentials.env` beside the settings document, reusing
`AgentSettingsFile.PathFromEnvironment`, so the two cannot disagree about the directory.

**Application (`AgentSession.SessionModel`, `PromptLibrary.ReviewInstruction`).** The only two additions, both
provider-agnostic. `SessionModel` is what `/model` writes and what a mode switch falls back to; without it, leaving a
plan turn that ran on the planner's model would leave the planner's model in place for the act turn. The review
instruction is prompt text, which is what `PromptLibrary` is for.

**Nothing in `Domain`.** No `AgentRole`, no credential type, no `PublicAPI.Unshipped.txt` line.

## How the loop uses it

- `RunCommand` resolves the model for `cli.Mode` once, and the preflight receives `RequestModels`.
- `ChatCommand` resolves the same model for a new session. A resumed transcript keeps the model it recorded: the
  session file is the authority on what that conversation was, and a role is a choice about this run.
- `/mode` calls `ApplyRoleModel`, which switches only when the model actually differs, and reports the reason
  (`the planner model` / `the session model`) on the diagnostics stream where the mode change already goes.
- `/review` handles its own turn in the loop rather than in `HandleCommand`, because it needs the agent, the observer
  and the cancellation split. It sets `Approval = ReadOnly`, switches to the reviewer's model when one is named, runs
  a turn with the review instruction, and restores both in a `finally` — the shape the budget path already uses.
- The banner gains one `roles` row, and only when a role was named.

## Complexity Tracking

| Choice | Why it is not the simpler thing | Cost |
|---|---|---|
| `AgentRole` in the CLI rather than in `Domain` | The alternative is a domain vocabulary for a host decision, plus `PublicAPI` churn, plus a word `Application` never reads | A host embedding the library cannot name a role; it configures one model per session, as before |
| Role text stored unresolved in `CliOptions` | Resolving inside `ApplyOption` makes `--planner X --provider Y` and `--provider Y --planner X` mean different things | An unresolvable value surfaces when the session opens, like a bad `--model` already does |
| No role map on `AgentSession` | A map is several public members for the three roles this round ships, and the loop never reads it | "Which roles are configured" stays a `CliOptions` question, so `ChatCommand` carries it |
| A pasted key set as a process environment variable | The alternative is a credential store that can be written to: a new port and a new lifetime | The variable is visible to this process's environment; children are stripped of it, and the file is the durable copy |
| Unix modes only, no Windows ACL | An ACL needs `System.IO.FileSystem.AccessControl`, a package the constitution does not carry | On Windows the file keeps the profile's ACL under `%APPDATA%`, which is already per-user; the limitation is in the XML doc and the README |
| `PromptLibrary.ReviewInstruction` in `Application` | Keeping prompt text in the CLI would put two prompts of one kind in two layers | One more public const in a class that is exactly the list of the product's prompts |

## Verification

- `AgentRoleOptionTests` — parsing, resolution, settings, environment, `RequestModels`, the `mcp serve` refusal.
- `ChatRoleTests` — `/roles`, `/mode` moving to the planner's model and back, a role read from the settings file, and
  `/review` running a read-only turn and restoring the approval mode.
- `CredentialPromptTests` — the paste, the file, the key never printed, the decline, the blank paste, no terminal, a
  provider that needs no key, one question per provider, and the preflight with and without a prompt.
- `CredentialFileUpsertTests` — create, replace in place, append, refuse a file that cannot be parsed, refuse a value
  with a line break, and the Unix mode.
- `HelpTextTests` pins the option table's columns, which the three new rows had to satisfy.

