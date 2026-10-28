# Contract: Tools

**Scope**: the tool seam in `SharpCoreAgent.Domain` and the six tools in `SharpCoreAgent.Infrastructure.Tools`.
Every tool is the only door to the operating system (Principle II).

## Seam

```csharp
public interface ITool
{
    ToolDefinition Definition { get; }   // name, description, JSON Schema string
    ToolSafety Safety { get; }           // ReadOnly or Destructive
    ValueTask<ToolResult> ExecuteAsync(ToolCall call, ToolInvocationContext context, CancellationToken ct);
}
```

Obligations:

1. **Validate first**: parse `call.ArgumentsJson`; on a missing/invalid field throw
   `ToolArgumentException` naming the field and its expected form. Never execute on bad input.
2. **Confine**: every path goes through `Workspace.TryResolve`; the tool then also rejects a path
   whose *physical* target (after resolving links, `File.ResolveLinkTarget`) leaves the workspace.
   A refusal returns `ToolResult.Refused`, not an exception.
3. **Bound**: text results are capped by the caller (`MaxToolResultChars`); tools additionally cap
   their own natural limits (file read 2 000 lines, search 200 matches, directory 500 entries,
   command output 64 KiB) and set `IsTruncated`, appending `[truncated]`.
4. **Classify effects**: report `ToolEffect`s (`Read`, `Created`, `Modified`, `Deleted`,
   `CommandRun`) with workspace-relative targets, so a report can list files and commands (FR-019).
5. **Never throw for expected failure**: missing file, non-zero exit, timeout and refusal are error
   `ToolResult`s whose `Content` is safe to hand back to the model.
6. **Idempotent reading**: two identical `read_file` calls return identical content; tools hold no
   state between calls.

## Tool catalogue

| Name | Safety | Effects | Arguments | Result |
|---|---|---|---|---|
| `list_directory` | ReadOnly | `Read` | `path?` (default `.`), `recursive?` (default `false`) | One entry per line: `d name/`, `f name` |
| `read_file` | ReadOnly | `Read` | `path` (required), `start_line?`, `line_count?` (default 2000) | Numbered lines `  12│text` plus a truncation marker |
| `search_text` | ReadOnly | `Read` | `pattern` (required, regex-free literal), `glob?` (default `*`), `path?` (default `.`, a file searches that file), `max_matches?` (default 100) | `relative/path:line: text` |
| `list_roots` | ReadOnly | `Read` | none | `primary` and `read-only` lines with absolute paths (007) |
| `write_file` | Destructive | `Created`/`Modified` | `path` (required), `content` (required), `overwrite?` (default `true`) | `wrote N bytes to <path> (<created|overwritten>)` |
| `delete_file` | Destructive | `Deleted` | `path` (required) | `deleted <path>` or error |
| `run_shell` | Destructive | `CommandRun` | `command` (required), `timeout_seconds?` (default 120, max 600), `working_directory?` | `$ <command>` + exit code + captured output |

### JSON Schemas (exact strings returned to the model)

`list_directory`
```json
{"type":"object","properties":{"path":{"type":"string","description":"Directory relative to the workspace root, or an absolute path inside an attachment. Defaults to the root."},"recursive":{"type":"boolean","description":"Recurse into subdirectories."}},"required":[],"additionalProperties":false}
```

`read_file`
```json
{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root."},"start_line":{"type":"integer","minimum":1},"line_count":{"type":"integer","minimum":1,"maximum":2000}},"required":["path"],"additionalProperties":false}
```

`search_text`
```json
{"type":"object","properties":{"pattern":{"type":"string","description":"Literal text to find (case-insensitive)."},"glob":{"type":"string","description":"Filename pattern such as *.cs. Defaults to *."},"path":{"type":"string","description":"Directory to search, relative to the workspace root or absolute inside a read-only attachment. Defaults to the workspace root; a file path searches that one file."},"max_matches":{"type":"integer","minimum":1,"maximum":500}},"required":["pattern"],"additionalProperties":false}
```

`list_roots` (007)
```json
{"type":"object","properties":{},"required":[],"additionalProperties":false}
```

`write_file`
```json
{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"overwrite":{"type":"boolean","description":"Defaults to true. When false, an existing file is an error."}},"required":["path","content"],"additionalProperties":false}
```

`delete_file`
```json
{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
```

`run_shell`
```json
{"type":"object","properties":{"command":{"type":"string","description":"Command line to execute in the workspace."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":600},"working_directory":{"type":"string","description":"Relative to the workspace root. Defaults to the root."}},"required":["command"],"additionalProperties":false}
```

## `run_shell` implementation notes

- Launcher: `cmd.exe /c` on Windows, `/bin/sh -c` on Linux — chosen at construction, not per call.
- The command string is passed as an argument to the shell; it is never `Invoke-Expression`'d or
  concatenated into a script file (Principle III).
- `ProcessStartInfo` sets `RedirectStandardOutput/Error`, `UseShellExecute=false`,
  `CreateNoWindow=true`, and a clean working directory inside the workspace.
- Timeout: `WaitForExitAsync(ct)` raced against `Task.Delay(timeout)`; on timeout `Kill(entireProcessTree: true)`
  and return an error result naming the timeout. Cancellation kills the tree too.
- Output: stdout then stderr, capped at 64 KiB total; a non-zero exit code is *not* an exception —
  it is a result carrying `exit code N` because a failing build is information the model needs.
- Environment: inherits the parent, minus variables whose names match `*_API_KEY`, `*_TOKEN`,
  `*_SECRET`, `*_PASSWORD` — the shell must not hand a provider key to an arbitrary command.

## Search and ignore rules

`search_text` and `list_directory` skip `.git`, `node_modules`, `bin`, `obj`, `.vs`, `.sharpcoreagent`,
and any file above 1 MiB or detected as binary (NUL byte in the first 8 KiB). These names are a
single constant so a test can assert the list.
