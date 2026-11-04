# Tasks: `.gitignore`-aware scanning

## Matcher

- [x] T001 Add `GitIgnoreMatcher` and `GitIgnoreRule` with Git-style parsing and last-match precedence.
- [x] T002 Support root, nested, anchored, directory-only, negated, wildcard and `**` rules.
- [x] T003 Treat malformed/unreadable ignore files as no rules rather than scan failures.

## Traversal

- [x] T004 Apply the matcher to `search_text`, including a search that starts in a subdirectory.
- [x] T005 Apply the matcher to `list_directory` files and directories.
- [x] T006 Apply the matcher to `WorkspaceFileScanner`; keep the fixed generated-directory list first.

## Verification

- [x] T007 Parser tests: root rules, nested rules, negation, anchored paths, directory-only rules, `**` and no rules.
- [x] T008 Scanner tests: search, list and index agree on ignored files/directories; subdirectory searches retain root
      rules.
- [x] T009 Update README, contract, spec, plan and quickstart.
- [x] T010 Run Release build, complete suite and recorded evaluation; record evidence.

## Evidence

- New focused coverage: 5 parser cases, 2 search cases, 1 list case and 1 index case.
- The fixed ignore list remains pinned by `WorkspaceFileScannerTests`.
- Tool descriptions changed to name `.gitignore`, so the recorded evaluation baseline moved by the description text only:
  57,493 → 57,823 input tokens, with no semantic regression. The accepted baseline is `eng/evals/baseline.json`.
- Release solution build: zero warnings. Complete suite: **1,616/1,616**. Evaluation: 3/3, 57,823 input / 260 output,
  0 unintended changes.

## Out of scope

Global excludes, `.git/info/exclude`, `.gitattributes`, sparse checkout, submodules, full Git edge-case parity, and
making `read_file` ignore-aware.
