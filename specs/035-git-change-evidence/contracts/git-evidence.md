# Contract: read-only Git evidence

**Feature**: `035-git-change-evidence`

Both tools are `ToolSafety.ReadOnly`, return no `ToolEffect`, and never accept a command, option or revision expression.
Their schema is `additionalProperties: false`.

## `git_status`

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Optional workspace-relative file or directory to scope the status to."
    },
    "timeout_seconds": { "type": "integer", "minimum": 1, "maximum": 120 }
  },
  "additionalProperties": false
}
```

Command:

```text
git --no-optional-locks --no-pager -c core.quotepath=false -c core.fsmonitor=false
    -c status.relativePaths=true status --porcelain=v1 -z --untracked-files=all
    --ignore-submodules=all -- <path or .>
```

Success:

```text
git status · 3 change(s)
 M tracked.txt
A  staged.txt
?? untracked.txt
```

- The first two columns are index (`X`) and worktree (`Y`) status.
- A rename/copy is one line: `R  old.txt -> new.txt`.
- A clean tree is `git status · 0 change(s)` followed by `clean working tree`.
- More than 500 entries ends with `[truncated: N more change(s)]`.
- The complete text is capped at 64 KiB; when capped, `IsTruncated` is true and `TextLimits.TruncatedMarker` appears.

## `git_diff`

```json
{
  "type": "object",
  "properties": {
    "scope": {
      "type": "string",
      "enum": ["working", "staged", "head"],
      "description": "working = unstaged changes; staged = index against HEAD; head = all tracked changes against HEAD. Defaults to working."
    },
    "path": {
      "type": "string",
      "description": "Optional workspace-relative file or directory to diff."
    },
    "timeout_seconds": { "type": "integer", "minimum": 1, "maximum": 120 }
  },
  "additionalProperties": false
}
```

Command:

```text
git --no-optional-locks --no-pager -c core.quotepath=false -c core.fsmonitor=false
    -c status.relativePaths=true -c diff.noprefix=false -c diff.mnemonicPrefix=false
    diff --no-ext-diff --no-textconv --no-color --unified=3 [--cached | HEAD] -- <path or .>
```

| `scope` | Comparison |
|---|---|
| `working` (default) | working tree against the index |
| `staged` | index against `HEAD` |
| `head` | working tree against `HEAD`, tracked files only |

Success:

```text
git diff head src/cart.cs
diff --git a/src/cart.cs b/src/cart.cs
...
```

- No output is `git diff <scope> <path>` followed by `no changes`.
- Untracked files are not present; `git_status` names them.
- Output is capped at 64 KiB and sets `IsTruncated` when cut.

## Failures

| Condition | `IsError` | Text |
|---|---|---|
| `scope` outside the enum | true | `scope must be 'working', 'staged' or 'head', not '<value>'.` |
| Path outside every readable root / through an escaping link | true | `PathSafety`'s refusal |
| `git` cannot start | true | `could not start 'git': <reason>. Is Git installed and on PATH?` |
| Workspace is not a repository | true | `git status failed with exit code <n>: <bounded stderr>` |
| Timeout | true | `git status did not finish within <n> seconds.` |
| Cancellation | thrown | `OperationCanceledException` reaches the turn loop |

## Invariants

1. No model-supplied string is interpreted as a Git option: it is a validated `scope` or a pathspec after `--`.
2. No repository-configured diff driver, textconv program, fsmonitor hook, pager or credential prompt runs.
3. No index write is requested; `--no-optional-locks` and `GIT_OPTIONAL_LOCKS=0` keep status read-only.
4. No network operation is possible: only `status` and `diff` are issued.
5. The child runs through `ShellProcessRegistry`, inherits no provider credential and is killed on timeout.
6. `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`, `GIT_COMMON_DIR` and Git's config-injection
   variables are removed; `GIT_LITERAL_PATHSPECS=1` keeps the confined path a path rather than pathspec magic.
