# Quickstart: `.gitignore`-aware scanning

No flag turns this on. `search_text`, `list_directory` and the index all read the workspace's own `.gitignore`.

```gitignore
# root .gitignore
*.log
!keep.log
build/
/root-only.txt
src/*.tmp
**/generated/**
```

Given that file:

- `app.log` is hidden; `keep.log` remains visible.
- `build/` is not entered, so generated files under it are never listed, searched or indexed.
- `root-only.txt` is hidden at the root but a same-named file below another directory is not.
- `src/a.tmp` is hidden; `src/a.cs` is visible.
- `generated/` at any depth is not walked.

A nested `src/.gitignore` adds rules only below `src`:

```gitignore
local.txt
!important.tmp
```

`src/local.txt` is hidden; `src/important.tmp` is re-included even if the root ignored `*.tmp`.

## Explicit paths

`read_file` is unchanged. `search_text` with a file path still searches that file even if a rule would hide it during a
walk. `list_directory` with an explicit directory still lists that directory; ignored children are omitted.

## Guarantees

- The built-in `.git`, `node_modules`, `bin`, `obj`, `.vs` and `.sharpcoreagent` exclusions always apply.
- A subdirectory search still applies the root and intermediate `.gitignore` files.
- A malformed or unreadable `.gitignore` contributes no rules and does not fail a read-only scan.
- The user's global Git excludes are not consulted; behavior is the same on every machine.

## Evidence

`GitIgnoreMatcherTests`, `SearchTextToolTests`, `ListDirectoryToolTests`/`ToolTests` and `WorkspaceFileScannerTests` cover
the parser and the three consumers. Run:

```powershell
dotnet test tests/SharpCoreAgent.Tests/SharpCoreAgent.Tests.csproj -c Release
```

See [`contracts/gitignore-scanning.md`](contracts/gitignore-scanning.md) for the exact rule table.
