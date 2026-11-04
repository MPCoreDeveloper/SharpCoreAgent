# Implementation Plan: Git change evidence

**Feature**: `035-git-change-evidence`
**Status**: implemented 2026-11-04

## Architecture

A shared `GitCommand` builds a fixed global argument prefix, starts `git` through the existing
`ShellProcessRegistry`, waits with the caller's timeout, drains UTF-8 stdout/stderr into one bounded string and returns
an exit code. `GitStatusTool` and `GitDiffTool` own only argument mapping, path confinement, rendering and failure
classification.

The registry is the same host `run_shell` and `dotnet_verify` use. This round adds no `Process.Start`; it adds two fixed
commands to a host that already owns process lifetime (Principle III). The shell still owns arbitrary commands, and
`GitStatusTool`/`GitDiffTool` never accept one.

## Fixed commands

Global prefix for both tools:

```text
git --no-optional-locks --no-pager
    -c core.quotepath=false
    -c core.fsmonitor=false
    -c status.relativePaths=true
    -c diff.noprefix=false
    -c diff.mnemonicPrefix=false
    <status|diff> ...
```

- `--no-optional-locks` prevents a status read from refreshing the index.
- `core.fsmonitor=false` prevents a repository-configured fsmonitor hook from running during a read.
- `core.quotepath=false` keeps non-ASCII paths readable; `-z` makes parsing unambiguous.
- `diff.noprefix`/`mnemonicPrefix` are pinned so the rendered paths stay stable.
- Environment: `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0`, `GIT_PAGER=cat`, `PAGER=cat`,
  `GIT_LITERAL_PATHSPECS=1`, `GIT_NO_REPLACE_OBJECTS=1`, `LC_ALL=C`, every provider credential removed by
  `ChildEnvironment.RemoveSecrets`, and every repository-redirection/config-injection variable (`GIT_DIR`,
  `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`, `GIT_COMMON_DIR`, `GIT_CONFIG_*` and their siblings)
  removed before the child starts.
- Working directory: the primary workspace root; pathspec always `.` or a confined workspace-relative path after `--`.

## Status rendering

Porcelain v1 `-z` records are `XY path`, with a second NUL record for a rename/copy source. The parser:

- ignores records shorter than four characters rather than guessing;
- turns `R`/`C` into `old -> new` and consumes both records;
- renders `XY path` so the model keeps the familiar index/worktree columns;
- counts beyond `MaxEntries = 500` and reports `[truncated: N more change(s)]`;
- applies `TextLimits.MaxCommandOutputChars` (64 KiB) to the final text and sets `IsTruncated`.

An empty record stream means a clean tree, which is rendered explicitly. Git's porcelain status already omits ignored
files by default.

## Diff scoping

| `scope` | Additional argument | Meaning |
|---|---|---|
| `working` (default) | none | worktree against the index |
| `staged` | `--cached` | index against `HEAD` |
| `head` | `HEAD` | tracked worktree against `HEAD` (staged and unstaged together) |

Untracked files are deliberately absent: Git does not diff a path it has never tracked. `git_status` names them, and
`read_file` reads them. `head` is the baseline a review normally wants.

## Failures

| Situation | Result |
|---|---|
| Bad scope | error: `scope must be 'working', 'staged' or 'head' ...` |
| Escaping path | refused with `PathSafety`'s explanation; Git never starts |
| Missing executable | error: `could not start 'git': ... Is Git installed and on PATH?` |
| Not a repository | error: `git status failed with exit code N: fatal: not a git repository ...` |
| Timeout | error naming the number of seconds; the process is stopped through the registry |
| Oversized output | success, `IsTruncated = true`, `TextLimits.TruncatedMarker` at the cut |

## Read-only proofs

- Both tools declare `ToolSafety.ReadOnly` and return no `ToolEffect`.
- `git status` uses `--no-optional-locks`; optional index writes are disabled.
- `git diff` uses `--no-ext-diff` and `--no-textconv`; no repository-configured diff program runs.
- No pager, editor, credential helper, network transport, hook, alias or revision expression is reachable from model input.
- Plan mode's existing read-only filter includes the tools without a code change. `/review` already sets
  `ApprovalMode.ReadOnly`, and read-only tools run there without a prompt.

## Options and prompt

`SharpCoreAgentOptions.GitToolsEnabled` defaults to `true`; `--no-git` maps to false. `AddGit` registers the two tool
singletons over the shared `ShellProcessRegistry`. `PromptLibrary.GitRule` ships only while the option is on, naming the
tools, the three scopes, the untracked caveat and why they are safe in Plan/Review.

## Complexity and limits

The tools observe only a repository whose root is the workspace or an ancestor of it, and they scope every command to
the workspace. Repository discovery from an attached root is out of scope. Submodule contents are ignored
(`--ignore-submodules=all`), so a nested repository can neither cause a second command nor hide inside a gitlink.
Rename/copy detection follows Git's defaults; a repository that disabled it reports delete plus add. Binary diffs are
Git's own `Binary files ... differ` line. There is no diffstat, blame or history, and no porcelain v2 branch/upstream
metadata.
