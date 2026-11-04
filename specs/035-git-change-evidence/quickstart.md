# Quickstart: Git change evidence

The two tools are registered by default. They run Git directly, so `git` must be on `PATH`; their presence does not add
network access or a shell.

## Inspect a working tree

```json
{ "path": "src" }
```

Call `git_status` with that argument (or `{}` for the whole workspace) during a Plan or Review turn. The answer names
staged (`M `), unstaged (` M`), added (`A `), deleted (`D `), renamed (`R `), conflicted (`UU`) and untracked (`??`)
paths. A rename is rendered as `old -> new`.

## Read the change

```json
{ "scope": "head", "path": "src/cart.cs" }
```

Call `git_diff`. The three scopes are:

- `working` — unstaged changes (the default);
- `staged` — the index against `HEAD`, i.e. what a commit would contain;
- `head` — every tracked change against `HEAD`, the usual review baseline.

Omitting `path` diffs the workspace root. An untracked file has no diff; `git_status` names it and `read_file` shows it.

## Turn it off

```powershell
sharpcoreagent chat --no-git
```

The tools and `PromptLibrary.GitRule` are absent from that process. Nothing else in the tool surface changes.

## What is deliberately impossible

There is no argument for a revision, a branch, a command, `--output`, a pager flag or an external diff driver. A path is
confined to the workspace and always travels after `--`. `git status` cannot refresh the index and `git diff` cannot run
repository-configured textconv, so reading evidence does not change the evidence.

## Evidence

```powershell
dotnet test tests/SharpCoreAgent.Tests/SharpCoreAgent.Tests.csproj -c Release
```

`GitToolTests` creates real temporary repositories and proves status, three scopes, path filtering, renames, clean trees,
truncation, invalid scope, escaping paths, option-looking paths, non-repository errors and a missing executable.
`GitToolRegistrationTests`, `GitOptionTests`, `PromptLibraryTests` and `TaskToolRegistrationTests` prove the default
surface, the opt-out and the twenty-three-tool set.
