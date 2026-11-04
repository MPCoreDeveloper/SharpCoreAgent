# Tasks: Git change evidence

## Setup

- [x] T001 Add `GitStatus`/`GitDiff` tool classes with their own stable names.
- [x] T002 Add `SharpCoreAgentOptions.GitToolsEnabled`, `CliOptions.Git`, `--no-git` and help text.
- [x] T003 Register both tools over the shared `ShellProcessRegistry`; add `PromptLibrary.GitRule`.

## Process and parsing

- [x] T004 Add a fixed-prefix `GitCommand` over the shell process host with no pager, optional lock, fsmonitor,
      prompt, external diff or textconv.
- [x] T005 Implement porcelain-v1 NUL parsing, rename expansion, 500-entry/64 KiB bounds and clean-tree rendering.
- [x] T006 Implement `working`/`staged`/`head` diff scopes, path confinement, 64 KiB bound and stable failures.

## Verification

- [x] T007 Add a real temporary-repository fixture and focused tool tests for status, all three diff scopes, path
      scoping, renames, clean trees, truncation, invalid scope, escaping path, option-looking path, non-repository,
      nested workspace and missing executable.
- [x] T008 Add registration, CLI, prompt and default-surface tests; move budget cases behind `--no-git` rather than
      raising their ceilings.
- [x] T009 Update README, contract, plan, spec and quickstart.
- [x] T010 Run Release build, the complete suite and the recorded evaluation baseline; audit for a new process/network
      door.

## Evidence

- Focused Git suite: 13 cases over real repositories created with `git init`, commits, staged edits, a rename, a
  workspace nested below the repository root and a filename that starts with `--`.
- Registration: both tools are `ReadOnly`, both disappear with `--no-git`, and the rule disappears with them.
- Default surface moves from 21 to **23** tools.
- Release build: zero warnings. Complete suite: **1,585/1,585**.
- Recorded evaluation baseline: 3/3 tasks, 57,493 recorded input / 260 output tokens, no semantic regressions after
  accepting the two new tool definitions and the Git rule into the baseline. The increase from 52,375 is those stable
  definitions and the rule, not tool output; each scenario gains about 1,700 estimated input tokens.
- Source audit: the only launch path is `GitCommand` -> `ShellProcessRegistry`; there is no `Process.Start`, shell
  string, network client or write operation in the Git tools.

## Out of scope

Any mutating Git command; arbitrary revisions, log/blame/show, stash, submodules, LFS, network transports, attached-root
repository discovery, diffstat, patch application and Git evidence in files outside the primary workspace.
