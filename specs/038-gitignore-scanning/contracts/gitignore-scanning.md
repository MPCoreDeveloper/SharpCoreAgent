# Contract: `.gitignore` scanning

**Feature**: `038-gitignore-scanning`

## Public seam

```csharp
public sealed class GitIgnoreMatcher
{
    public static GitIgnoreMatcher For(string root);
    public void LoadFrom(string directory);
    public void LoadAncestors(string path);
    public bool IsIgnored(string fullPath, bool isDirectory);
}
```

- `For` loads the root `.gitignore`.
- `LoadFrom` loads one directory's file once; a missing/unreadable file is a no-op.
- `LoadAncestors` loads every file from the root through the file or directory named.
- `IsIgnored` returns the result of the **last matching rule**; no rule means visible.

## Syntax

| Input | Result |
|---|---|
| blank line / `# comment` | no rule |
| `*.log` | basename matches at any depth below its directory |
| `!keep.log` | negates earlier matches when it is last |
| `build/` | directory-only |
| `/root.txt` | anchored to the owning directory |
| `src/*.tmp` | path relative to the owning directory; `*` does not cross `/` |
| `**/generated/**` | crosses directories; `**/` may match zero directories |
| `?` | one character except `/` |
| `[abc]`, `[!abc]` | one character/range or its negation |

Escaped `\#` and a leading `!` keep Git's comment/negation meaning. Trailing spaces are trimmed. Malformed classes are
ignored with their line.

## Scanner rules

### `search_text`

- A directory walk skips an ignored directory before reading it and an ignored file before searching it.
- A file supplied as `path` is searched even if a rule would ignore it; a direct request is not a traversal.
- Starting below the root still loads the root's and intermediate `.gitignore` files.

### `list_directory`

- Files and directories whose path is ignored are omitted.
- The explicit directory named by `path` is listed; its ignored children are omitted.
- Non-recursive and recursive walks use the same rules.

### Index

- `WorkspaceFileScanner` skips an ignored directory and an ignored file before it is read or represented as a row.
- The fixed list `.git`, `node_modules`, `bin`, `obj`, `.vs`, `.sharpcoreagent` applies first and always.
- `.gitignore` itself is included when no rule hides it; it is not special-cased as a source file.

## Invariants

- An ignored directory is not entered, so a nested `.gitignore` below it is not loaded.
- Last matching rule wins, including negation; a path below an ignored directory is ignored even if a later rule names
  the child, because Git never reads a nested file below an ignored parent.
- The three scanners agree on the visible path set for the same tree and root.
- No process, network, write, setting or tool is added.
- Only workspace-local `.gitignore` files are read; global excludes and `.git/info/exclude` are not. A symlinked
  `.gitignore` is ignored.
