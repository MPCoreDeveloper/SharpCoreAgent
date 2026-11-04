# Feature Specification: Git change evidence

**Feature**: `035-git-change-evidence`
**Status**: implemented 2026-11-04

## Problem

Plan and Review hide `run_shell`, because it can run anything and cannot be approved inside a read-only turn. They
still need the one fact a code review starts from: what changed. Without it the reviewer reads whole files and
reconstructs a diff by memory, or trusts the model's prose about edits nobody can inspect. The same gap makes a Plan
turn unable to answer "what is staged?" without asking the developer to run Git outside the conversation.

## What ships

Two fixed-shape, read-only tools, registered by default:

| Tool | Question | Scope |
|---|---|---|
| `git_status` | Which paths are staged, unstaged, untracked, renamed or conflicted? | optional workspace path |
| `git_diff` | What are the tracked changes? | `working`, `staged` or `head`; optional workspace path |

Both are `ToolSafety.ReadOnly`, so the existing Plan filter advertises them and the Review loop's `ReadOnly` approval
mode lets them run. The shell remains the only tool that can run an arbitrary command.

`--no-git` / `SharpCoreAgentOptions.GitToolsEnabled = false` removes both tools and `PromptLibrary.GitRule`.

## User stories

1. **Review an edit.** `/review` asks what changed; the reviewer calls `git_diff { "scope": "head" }`, reads the unified
   diff, and cites lines rather than paraphrasing a file it read before the edit.
2. **See the index.** A developer wants to know what a commit would contain; `git_status` distinguishes `M ` (staged) from
   ` M` (unstaged) and names untracked files that no diff can show.
3. **Narrow a large change.** A turn passes `path: "src/SharpCoreAgent.Application"` so a repository-wide diff does not
   spend the context budget on unrelated files.
4. **Stay safe in Plan.** A Plan turn inspects the working tree without a shell approval and without gaining a tool that
   can mutate it.

## Functional requirements

- **FR-001**: `git_status` takes only optional `path` and `timeout_seconds`; the command is
  `git --no-optional-locks --no-pager ... status --porcelain=v1 -z --untracked-files=all --ignore-submodules=all -- <path or .>`.
- **FR-002**: `git_status` parses NUL-delimited records, renders index/worktree codes, expands a rename/copy into
  `old -> new`, strips NUL, bounds entries at 500 and the whole answer at 64 KiB, and says when either limit dropped data.
- **FR-003**: `git_diff` takes a closed `scope` enum (`working`, `staged`, `head`) and optional `path`/`timeout_seconds`.
  It maps to no revision argument, `--cached`, or `HEAD`; any other scope is a tool failure, not a revision expression.
- **FR-004**: Diffs are `--no-ext-diff --no-textconv --no-color --unified=3`, followed by `--` and the workspace path or
  `.`. A repository-configured external command is never started to decorate a diff.
- **FR-005**: The optional path passes `PathSafety.TryResolve`, so `..`, a link out of the root and an attachment path the
  tool cannot observe are refused before Git starts. The path is converted to a workspace-relative Git pathspec and is
  always placed after `--`.
- **FR-006**: Git runs in the workspace root with no shell, no pager, no terminal prompt, UTF-8 output, secrets removed,
  repository-redirection/config-injection variables removed, literal pathspecs and `GIT_OPTIONAL_LOCKS=0`. The child uses
  the existing `ShellProcessRegistry`, so it is tracked, cancelled and killed on timeout with every other child process.
- **FR-007**: No output means `clean working tree` or `no changes`; a non-zero exit is an error result containing Git's
  bounded stderr; a missing executable, non-repository workspace and timeout are stable error results, never exceptions.
- **FR-008**: Both tools are registered by default, report no file effects, and are removed together with their prompt
  rule by `--no-git`.
- **FR-009**: No new `Process.Start`, network call, write capability or shell interpolation is added. Git arguments travel
  in `ProcessStartInfo.ArgumentList`.

## Non-goals

- Staging, committing, checking out, resetting, cleaning, fetching, pulling, pushing, cloning or any other mutating Git
  command.
- Arbitrary revisions (`HEAD~1`, `branch...HEAD`), `git log`, blame, show, reflog, submodule operations, stash, notes, LFS
  or `--follow`.
- Repository discovery outside the workspace root, or Git evidence for read-only attached roots. A secondary root may not
  be a repository the primary invocation can observe safely.
- Diffstat presentation, syntax highlighting, images in diffs, or applying a patch.

## Success criteria

- A real temporary repository shows staged, unstaged, untracked and renamed paths correctly, and the three diff scopes
  show the three comparisons.
- A 4,000-line rewrite is truncated with the documented marker, not returned whole.
- A non-repository workspace, a missing Git executable, a bad scope and an escaping path each return an error result.
- Plan and Review can call both tools without an approval prompt; the tools cannot write, delete or start a shell.
- The full suite and the recorded evaluation baseline remain green.
