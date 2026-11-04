# Feature Specification: `.gitignore`-aware scanning

**Feature**: `038-gitignore-scanning`
**Status**: implemented 2026-11-04

## Problem

`search_text`, `list_directory` and the indexer each skip a fixed set of generated directories, but none reads the
repository's own `.gitignore`. A workspace can therefore search, list and index files the developer deliberately
excluded — generated output, local secrets, large build artefacts — and the three views can disagree about which paths
exist.

## What ships

- One `GitIgnoreMatcher` in Domain, used by `search_text`, `list_directory` and `WorkspaceFileScanner`.
- Root and nested `.gitignore` files, loaded only while walking the tree.
- Git-style comments, blank lines, negation (`!`), directory-only rules, anchored paths, `*`, `?`, character classes
  and `**`.
- Last matching rule wins, matching Git's precedence.
- The built-in generated-directory list remains in force even when a workspace has no `.gitignore`.
- Search and list paths inside read-only attachments use the attachment's own `.gitignore`.

## User stories

1. **Generated files stay out.** A repository ignores `coverage/` and `*.generated.cs`; all three scans omit them.
2. **A local rule is enough.** Adding a line to the root `.gitignore` changes the next search, listing and index refresh
   without a restart.
3. **Nested rules are local.** `src/.gitignore` can ignore `local.txt` without hiding another directory's file.
4. **An exception is respected.** `*.log` followed by `!keep.log` keeps `keep.log` visible.
5. **The model can still read an explicit path.** `read_file` is unchanged; scanning controls traversal, not a direct
   request for a known file.
6. **A subdirectory search still obeys the root.** Searching `src` does not lose the root's rules.

## Functional requirements

- **FR-001**: `GitIgnoreMatcher.For(root)` loads the root `.gitignore`; `LoadFrom(directory)` loads one nested file once;
  `LoadAncestors(path)` loads every applicable file from the root through that path.
- **FR-002**: An ignored directory is not entered, so its nested `.gitignore` is never loaded. This is Git's traversal
  rule and the reason a parent rule cannot be undone from inside an ignored tree.
- **FR-003**: Rules are evaluated in load order; the last match decides. A `!` rule reverses an earlier match.
- **FR-004**: A blank line or `#` comment is ignored. `\#` and a leading `!` retain Git's comment/negation meanings.
- **FR-005**: A trailing `/` makes a rule directory-only. A leading `/` anchors it to the directory that owns the file.
  A pattern without `/` matches a name at any depth below that directory. `*`, `?`, `[]` and `**` follow the documented
  Git forms.
- **FR-006**: `search_text` applies the matcher to directories and files while walking; an explicit file path is still
  searched, because it is a direct request rather than a traversal.
- **FR-007**: `list_directory` omits ignored directories and files. It still lists the explicit directory the caller
  named.
- **FR-008**: `WorkspaceFileScanner` skips ignored directories and files before reading or producing a row. The fixed
  generated-directory list is checked first, so it applies with no `.gitignore`.
- **FR-009**: An unreadable or malformed ignore file contributes no rules and never fails a read-only scan. A symlinked
  `.gitignore` is not read, and a malformed character class is skipped rather than crashing the walk.
- **FR-010**: Only workspace-local `.gitignore` files are read. Global excludes and `.git/info/exclude` are deliberately
  not consulted, so behavior is identical on every machine and CI image.
- **FR-011**: No new process, network, settings or tool is added.

## Non-goals

- `.git/info/exclude`, the user's global excludes file, `.gitattributes`, sparse checkout, submodule ignore rules and
  nested repositories outside the workspace.
- Reimplementing every Git edge case: escaped spaces, backslash escapes beyond `#`/`!`, locale-dependent case folding,
  and `**` combinations not covered by the contract.
- A `.gitignore`-aware `read_file`; a direct read remains a direct read.
- Watchers or live refresh outside the existing index/search invocation boundaries.

## Success criteria

- A real temporary tree with root and nested rules yields the same visible file set from search, listing and the index.
- Negation, anchored paths, directory-only rules and `**` each have a focused test.
- A subdirectory search still applies the root rules.
- The fixed generated-directory list still works without a `.gitignore`.
- Release build, complete suite and recorded evaluation remain green (with the accepted tool-description token delta).
