# Tool contracts — `059-capability-gap-closure`

Every new tool is an `ITool` with a fixed `ToolDefinition(Name, Description, ParametersSchemaJson)`, a
`ToolSafety`, and a recorded `ToolResult`. The schemas below are the exact `ParametersSchemaJson` strings.

## Slice 1 — Git write tools

### `git_stage` — `Destructive`

```json
{"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Workspace-relative files or directories to stage."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"required":["paths"],"additionalProperties":false}
```

Effect: `CommandRun("git add -A -- <paths>")` on success. No file effect is reported (Git touched the index, not a
tracked file's content).

### `git_commit` — `Destructive`, implements `IToolPreview`

```json
{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":4000,"description":"The commit message. First line is the subject (keep it under 72 characters)."},"paths":{"type":"array","items":{"type":"string"},"description":"Optional workspace-relative paths to include; omit to commit whatever git_stage staged."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"required":["message"],"additionalProperties":false}
```

Effect: `CommandRun("git commit ...")` on success; result names the short SHA and file count. `IToolPreview` returns
the proposed subject and the paths that would be committed. Hooks/signing are disabled (see plan); the author is the
user's configured identity.

### `git_branch` — `Destructive`

```json
{"type":"object","properties":{"op":{"type":"string","enum":["list","create","delete"],"description":"list prints branches; create/delete take a name."},"name":{"type":"string","description":"Branch name for create/delete; validated by git check-ref-format --branch."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":120}},"required":["op"],"additionalProperties":false}
```

`list` reports no effect and is safe to call in any mode the model can reach; `create`/`delete` report
`CommandRun`. `delete` refuses the checked-out branch.

## Slice 2 — `dotnet_verify` schema delta

Add two optional properties to the existing object (everything else unchanged):

```json
"filter":{"type":"string","description":"Test-name filter (test only); passed as --filter. A value matching no test is a failure."},
"no_build":{"type":"boolean","description":"Test only: run against already-built binaries with --no-build. Does not compile, so it does not satisfy the compile gate."}
```

`kind=build` refuses both with a `ToolResult.Failure`. The result's `CountsAsVerification` is `false` when
`no_build` is true, `true` otherwise.

## Slice 3 — Project memory

### `project_memory` — `ReadOnly`

```json
{"type":"object","properties":{"op":{"type":"string","enum":["list","get"],"description":"list prints note names and timestamps; get returns one note by name."},"name":{"type":"string","description":"Note slug for get: [a-z0-9-]."}},"required":["op"],"additionalProperties":false}
```

Reported effect: `Read(".sharpcoreagent/memory")`. A missing store is an empty `list`, not an error; `get` of a
missing name is a `ToolResult.Failure` naming the slug.

### `remember` — `Destructive`

```json
{"type":"object","properties":{"name":{"type":"string","description":"Note slug; lowercased, spaces to dashes, only [a-z0-9-] accepted."},"body":{"type":"string","description":"The note text (UTF-8, max 16 KiB). Written verbatim; never executed."}},"required":["name","body"],"additionalProperties":false}
```

Reported effect: `Created`/`Modified(".sharpcoreagent/memory/<slug>.md")`. The result echoes the slug and byte
count, never the body. `readonly` refuses it; `ask` prompts.

## Safety and effects summary

| Tool | `ToolSafety` | Effect kinds | Approval in `readonly` / `ask` / `allow` |
|---|---|---|---|
| `git_stage` | `Destructive` | `CommandRun` | refused / prompts / runs |
| `git_commit` | `Destructive` | `CommandRun` | refused / prompts / runs |
| `git_branch` | `Destructive` | `CommandRun` (create/delete) | refused / prompts / runs |
| `dotnet_verify` | `Destructive` (unchanged) | `CommandRun` | refused / prompts / runs |
| `project_memory` | `ReadOnly` | `Read` | runs / runs / runs |
| `remember` | `Destructive` | `Created`/`Modified` | refused / prompts / runs |

## Verification-gate contract (FR-012)

```text
lastVerification[profile] rises only when:
    !result.IsError
 && !result.IsRefused
 && result.ToolName == binding.ToolName
 && result.CountsAsVerification          // NEW — false for a no_build test run
```

`ToolResult.CountsAsVerification` defaults to `true`, so this is a no-op for every existing tool and result. It is
a public addition recorded in `SharpCoreAgent.Domain`'s `PublicAPI.Unshipped.txt`.

## Failure contract (all new tools)

| Situation | Result |
|---|---|
| Unknown `op` / bad enum | `ToolResult.Failure` naming the allowed values |
| Escaping path / option-looking path | `ToolResult.Refused` from `PathSafety`; Git never starts |
| Invalid branch name | `ToolResult.Failure` from `git check-ref-format --branch` |
| Commit with nothing staged | `ToolResult.Failure` carrying Git's "nothing to commit" text |
| Missing `git` / non-repository workspace | stable `ToolResult.Failure` (same shape as `git_status`) |
| Timeout | `ToolResult.Failure` naming the seconds; process stopped via the registry |
| Note too large / too many notes / bad slug | `ToolResult.Failure` naming the ceiling |

