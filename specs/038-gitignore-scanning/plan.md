# Implementation Plan: `.gitignore`-aware scanning

**Feature**: `038-gitignore-scanning`
**Status**: implemented 2026-11-04

## Architecture

`GitIgnoreMatcher` lives in `SharpCoreAgent.Domain` because both `SharpCoreAgent.Infrastructure` and
`SharpCoreAgent.Indexing` need the same rules and neither may depend on the other. It is a BCL-only utility: it reads
workspace files and has no vendor, process or network dependency.

The parser is `GitIgnoreRule`, one internal record per non-comment line. A rule stores its base directory relative to
the workspace root, whether it is negated, whether it is directory-only, and a compiled regular expression. Matching
uses only the workspace-relative path, with `/` separators.

## Load order

```text
For(root)                 loads root .gitignore
scanner enters a dir      LoadFrom(dir) before enumerating its children
search starts below root LoadAncestors(path) before the first step
```

The matcher remembers loaded directories by relative path. An ignored directory is never pushed onto a walker's stack,
so its `.gitignore` is never loaded. This is not an optimization: Git does not let a nested file re-include a path under
an ignored parent, and the scanner must agree.

## Rule semantics

| Form | Meaning |
|---|---|
| `*.log` | a basename at any depth below the owning directory |
| `!keep.log` | the last matching rule wins, so this can re-include a name |
| `build/` | directories only |
| `/root.txt` | anchored to the owning directory |
| `src/*.tmp` | a path relative to the owning directory |
| `**/generated/**` | any depth; `**/` can match zero directories |
| `[abc]`, `?` | one character classes and wildcards; `*` never crosses `/` |

Malformed rules are ignored with their line; an unreadable `.gitignore` leaves the tree visible. Read-only scanning must
not fail because an ignore file is unusual.

## Scanners

- `SearchTextTool` creates a matcher for the root that answered the path (workspace or attachment), calls
  `LoadAncestors` for the requested root, and loads each directory as it pops. It skips an ignored directory before
  pushing it and an ignored file before searching it. A file passed explicitly is still searched.
- `ListDirectoryTool` uses the same matcher and filters both files and directories. It still lists the explicit
  directory it was asked about.
- `WorkspaceFileScanner` owns one matcher for a scan, loads each directory before `PushChildDirectories` and
  `CollectFiles`, and checks the fixed generated-directory list first.

The fixed list remains authoritative for `.git`, `node_modules`, `bin`, `obj`, `.vs` and `.sharpcoreagent`, even when no
`.gitignore` exists. That is what keeps an empty or hostile `.gitignore` from making the index scan generated trees.

## Complexity and limits

- One regex per rule, compiled and bounded by a one-second match timeout. Typical repositories have tens of rules.
- Nested ignore files are loaded only along directories actually traversed; no full-tree pre-scan.
- Global Git configuration is intentionally excluded; the contract is the repository's own files.
- A directory path explicitly named by a caller is still listed/searched, because the caller named it rather than
  discovering it through a walk.
