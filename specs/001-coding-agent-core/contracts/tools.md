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
| `view_image` | ReadOnly | `Read` | `path` (required), `max_dimension?` (64–4096), `rotation?` (90/180/270) | `'<path>' (<media type> · <n> bytes[, resized <w> × <h> PNG][, rotated <d>° to <w> × <h>]) is attached to the next message.` and the image itself, on the user message that follows (017) |
| `write_file` | Destructive | `Created`/`Modified` | `path` (required), `content` (required), `overwrite?` (default `true`) | `wrote N bytes to <path> (<created|overwritten>)` |
| `delete_file` | Destructive | `Deleted` | `path` (required) | `deleted <path>` or error |
| `run_shell` | Destructive | `CommandRun` | `command` (required), `timeout_seconds?` (default 120, max 600), `working_directory?` | `$ <command>` + exit code + captured output |
| `dotnet_verify` | Destructive | `CommandRun` | `kind` (required: `build` or `test`), `project?`, `timeout_seconds?` (default 300, max 900) | Labelled header (exit code, passed/failed, warnings, errors, test counts, duration) + the SDK's log (012) |
| `python_verify` | Destructive | `CommandRun` | `target?` (file or directory), `timeout_seconds?` (default 300, max 900) | Labelled header + `python -m unittest discover -v` output (040) |
| `node_verify` | Destructive | `CommandRun` | `target?` (file or directory), `timeout_seconds?` (default 300, max 900) | Labelled header + `node --test` output (040) |
| `git_status` | ReadOnly | none | `path?`, `timeout_seconds?` (default 30, max 120) | Porcelain index/worktree status, one `XY path` per line, renames as `old -> new` (035) |
| `git_diff` | ReadOnly | none | `scope?` (`working`/`staged`/`head`), `path?`, `timeout_seconds?` (default 30, max 120) | One unified diff, or `no changes` (035) |
| `task` | Destructive | the child's `Created`/`Modified`/`Deleted`/`CommandRun` | `prompt` (required), `description?` (≤ 120, defaults to the prompt's first line), `max_iterations?` (1–24, default 12) | `task · <description> · <N> model calls · <T> tokens` + the child's final message, verbatim (011) |

The catalogue above is round `001`'s six tools and the three that round `003` added around `run_shell`. Later
rounds keep their own contracts and are the reference for the tools they add: `replace_in_file`,
`shell_output` and `shell_stop` in `specs/003-terminal-console-agent`, the five index tools in
`specs/002-code-evidence-index`, `fetch_url` in [`specs/008-web-fetch`](../../008-web-fetch/contracts/web-fetch.md),
and `dotnet_verify` in [`specs/012-compiler-oracle`](../../012-compiler-oracle/contracts/dotnet-verify.md), and
`python_verify`/`node_verify` in [`specs/040-runtime-verifiers`](../../040-runtime-verifiers/contracts/runtime-verifiers.md),
and `task` in [`specs/011-subagent-task`](../../011-subagent-task/contracts/task-tool.md), and `view_image` — with the
`ImageContent` a user message carries and the wire shape that sends it — in
[`specs/017-image-view`](../../017-image-view/contracts/view-image.md).

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

`view_image` (017)
```json
{"type":"object","properties":{"path":{"type":"string","description":"Image path relative to the workspace root."},"max_dimension":{"type":"integer","minimum":64,"maximum":4096,"description":"Downscale the picture to this largest width or height, preserving its aspect ratio; omit to show the file byte for byte."},"rotation":{"type":"integer","enum":[0,90,180,270],"description":"Rotate clockwise by this many degrees after any rendering or downscale; omit or 0 to show the file as it is."}},"required":["path"],"additionalProperties":false}
```
Note: the extension alone decides the media type (`.png`, `.jpg`/`.jpeg`, `.gif`, `.webp`), the file is read only
under 4 MiB, and the model's own `SupportsImages` decides whether the bytes are read at all — a text-only session is
refused *before* the file is opened, and the refusal says not to describe the picture. The image does not travel on
the tool role: the loop attaches it to the user message that follows the result.

`fetch_url` (008 — registered only when `--web` is passed)
```json
{"type":"object","properties":{"url":{"type":"string","description":"Absolute http or https URL to read."},"max_chars":{"type":"integer","minimum":1,"maximum":60000,"description":"Longest text to return; defaults to the tool's ceiling."}},"required":["url"],"additionalProperties":false}
```
Note: the address rule is applied twice — before each request and again inside the connect callback — and
the fetch client refuses a proxy (`UseProxy = false`), because through a proxy the host is resolved by
something the developer never named on the command line. A refused address is a tool result naming the
category and the `--web-allow` that would permit it.

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

`search_text` and `list_directory` skip `.git`, `node_modules`, `bin`, `obj`, `.vs`, `.sharpcoreagent`, any path covered
by a workspace-local `.gitignore`, and any file above 1 MiB or detected as binary (NUL byte in the first 8 KiB). The
fixed names are one constant so a test can assert the list; the `.gitignore` matcher is shared with
`WorkspaceFileScanner` so search, listing and the index agree. The full rule table is
[`specs/038-gitignore-scanning/contracts/gitignore-scanning.md`](../../038-gitignore-scanning/contracts/gitignore-scanning.md).

## Server-provided tools (009)

`--mcp "<command>"` starts a stdio MCP server and registers every tool it lists. These are the only tools
whose schema this build does not write, and the only ones whose names are generated:

- **Name** — `<server>_<tool>`, folded to `[a-z0-9_]` with runs collapsed, ends trimmed and the whole name
  capped at 64 characters. `<server>` is the command's executable stem (`npx`, `uvx`, `mcp-server-git`);
  a second server with the same stem gets `_2`, and a tool name that folds onto one already used inside
  the same server gets `_2` as well. A collision with a built-in is not renamed: `ToolRegistry` refuses
  the duplicate at construction.
- **Description** — the server's own, trimmed and capped at 1 024 characters; the command line when the
  server sent none.
- **Schema** — the server's `inputSchema`, forwarded verbatim; `{"type":"object","properties":{}}` when it
  sent none.
- **Safety** — `ReadOnly` only for an explicit `annotations.readOnlyHint: true`. Everything else is
  `Destructive`, so it is gated by the approval mode and prompts in `ask`.
- **Result** — `content[]` flattened with every entry described (text verbatim; `image`, `audio` and
  `blob` by MIME type and size; resources and resource links by URI), joined with a blank line, bounded
  at 64 000 characters with `[truncated]`, and bounded again by `MaxToolResultChars` for the turn.
  `isError: true` becomes a failed `ToolResult`, and a JSON-RPC error or a dead server becomes a failure
  naming the code or the exit status rather than an exception.
- **Effects** — a destructive call reports `CommandRun` with `<server>: <tool>`; a read-only call reports
  none, because there is no workspace path to name.
- **Lifetime** — one child per flag, started before the first turn and killed with its process tree when
  the process ends. A server that will not answer its handshake is a startup failure and `exit 2`.

## Language-provided tools (015, 034)

Three read-only tools answer from a compiler linked into the process rather than from the index. They are
language-neutral: each takes a Domain port (`ICodeDiagnostics`, `ISymbolUses`, `ISignatureHelp`), and
`LanguageRouter` picks the adapter whose `ILanguageAdapter.Supports` claims the path. C# is registered by
default; `--no-csharp` removes it. `--fsharp` adds the F# adapter behind the same tools. The language-specific
contracts are [`specs/015-fsharp-peer`](../../015-fsharp-peer/contracts/code-intelligence.md) and
[`specs/034-csharp-compiler-oracle`](../../034-csharp-compiler-oracle/contracts/code-intelligence.md); the
three names and their arguments are:

- **`file_diagnostics`** — `path`. Answers `path:line:column · severity id · message` per diagnostic, or
  `path · no diagnostics` for a clean file. Success and failure are different sentences.
- **`uses_at`** — `path`, `line` (≥ 1), `column` (≥ 0). Answers `<display> at path:line:column · n uses ·
  n files`, then one `definition …` or `use …` line per site.
- **`signature_at`** — `path`, `line`, `column`. Answers the overloads at a call site, numbers them, and
  marks the argument the position sits inside with `← active`.

Every failure carries a stable code — `FSH001`–`FSH006` from the F# adapter, `CSH001`–`CSH006` from the C#
adapter, `LANG001` for an extension no adapter claims, `LANG002` for an adapter that claims a file without
implementing the port — and a hint that names a registered tool. None of them closes the turn's verification
gate: only the verifier registered for the edited path's profile does.

## Git evidence (035)

`git_status` and `git_diff` answer what changed without exposing a shell. They are `ReadOnly` with no effects, so Plan
mode and a `readonly` session call them and no approval prompt appears. The full schema, output and failure contract is
[`specs/035-git-change-evidence/contracts/git-evidence.md`](../../035-git-change-evidence/contracts/git-evidence.md).
What a reader of this catalogue needs:

- **Fixed commands** — the model supplies only a closed `scope` and a confined `path`. No command, revision expression,
  option, ref or output file is reachable; a path travels after `--`.
- **Read-only evidence** — `--no-optional-locks` and `GIT_OPTIONAL_LOCKS=0` prevent an index refresh, fsmonitor is off,
  and `git diff` disables external diff drivers and textconv. `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE` and other
  repository/config-injection variables are removed, and literal pathspecs stop a path after `--` becoming Git magic.
  There is no pager, editor, hook, credential prompt or network transport.
- **Process host** — Git starts through the existing `ShellProcessRegistry`, not a new launch path, inherits no provider
  credential and is killed on timeout.
- **Bounds** — status entries 500, all output 64 KiB; truncation sets `IsTruncated` and appends the shared marker.
- **Scope** — the command runs in the primary workspace root and every pathspec is scoped to it. Attached roots are not
  searched for repositories.
- **Opt-out** — `--no-git` removes both tools and `PromptLibrary.GitRule`; the rest of the registry is unchanged.

## Verification profiles (037)

A turn may not complete after editing a profile-owned file until the verifier that owns that profile has passed since
the edit. The rule lives in `VerifyGate`; the language names do not. A tool implements `IVerificationTool` and declares
`VerificationProfile(Name, DisplayName, Extensions)` records. The tool's own name is what the gate waits for and what
the nudge names.

- **Built-in** — `dotnet_verify` declares `dotnet` / `.NET` with the existing source/build extensions. Its command,
  schema, parser, prompt and exact unverified sentence are unchanged. `python_verify` declares `python` / `Python` with
  `.py` and `.pyw`, and `node_verify` declares `node` / `JavaScript` with `.js`, `.mjs` and `.cjs` (040).
- **Opt-in** — a host or language adapter can register another tool such as `ruby_verify` with `ruby` / `.rb`; editing
  `app.rb` then creates a Ruby gap that only a later passing `ruby_verify` closes.
- **Order** — the last matching edit and last successful verifier result are compared by result index, so a
  verification that ran before the edit does not close it, and a failed/refused result does not count.
- **No tool, no gate** — profiles come from registered verifier tools. A registry with none has no bindings and no
  nudge, preserving the old host-without-`dotnet_verify` behavior.
- **Wording** — `PromptLibrary.VerifyNudgeFor` and `VerifyGate.Detail` name the display language and tool, with the
  single .NET gap returning the legacy sentence exactly.

The full contract is [`specs/037-verification-profiles/contracts/verification-profiles.md`](../../037-verification-profiles/contracts/verification-profiles.md).

## Checkpoints (036)

The turn loop opens one `ITurnCheckpoint` per turn and passes it through `ToolInvocationContext.Checkpoint`. The three
file-mutating tools capture the target after validation and before mutation; a capture failure refuses the write. The
journal, the `/undo` output and the restore rules are in
[`specs/036-turn-checkpoints/contracts/checkpoints.md`](../../036-turn-checkpoints/contracts/checkpoints.md). What a
reader of this catalogue needs:

- **Coverage** — `write_file`, `replace_in_file` and `delete_file` only. `run_shell`, MCP tools and `dotnet_verify` do
  not report per-file mutations the journal can capture.
- **Turn grouping** — a top-level turn with a net change is one LIFO undo step; a `task` child merges into its parent;
  empty, plan and review turns are discarded.
- **Safety** — exact pre-turn bytes or absence are stored; restore refuses a file that changed after the turn ended and
  keeps the checkpoint for retry.
- **Lifetime** — process-local memory, 64 MiB per captured file, no persistent storage and no new process or network
  call.
- **Command** — `/undo` exists only in `chat` and does not run a model turn.

## Pictures (017)

`view_image` is the only way a picture enters a conversation, and it is registered in every build: reading an image is
a read, not an opt-in capability, so no flag adds it and none removes it. The full contract — schema, refusal
sentences, the carrier message and the `image_url` part — is
[`specs/017-image-view/contracts/view-image.md`](../../017-image-view/contracts/view-image.md). What a reader of this
catalogue needs is where the tool sits among the others:

- **Safety** — `ReadOnly`, and the only effect is `Read`, on the file, workspace-relative (or relative to the attached
  root that owns it). A plan turn and a `readonly` session call it and no prompt appears.
- **Arguments** — `path`, an optional `max_dimension` (64–4096) that asks for a smaller copy, and an optional
  `rotation` (90/180/270) applied after it. There is no crop, no page and no URL; without either transformation the
  developer's file is shown as it is, or refused with the number that says why.
- **Result** — one sentence naming the file, its media type and its size, plus one `ImageContent` in
  `ToolResult.Images`. The text is what a model that cannot see, and a person reading the transcript, get instead.
- **Where the bytes go** — not on the tool role. `CodingAgent` appends a user message carrying
  `PromptLibrary.ImageAttached` and the image after the tool message, because every compatible provider accepts image
  parts on the user role and none accepts them on a tool result. The loop reads `ToolResult.Images`, so this is a
  property of any tool that answers with a picture rather than of this one.
- **Refusals** — a text-only model, and a normal turn whose context named no model, are refused before the file is
  opened, and the refusal forbids describing a picture the model never received. An MCP server is the one
  model-free caller allowed: it returns the image and lets the client choose the model. Wrong extension, zero bytes
  and over 4 MiB are refused with their own numbers. Every one is a failed `ToolResult` the model can act on, never an
  exception.
- **Prompt** — operating rule 9 of `PromptLibrary.SystemPrompt` ships with it: call `view_image` when the answer
  depends on what a picture contains, and say so when the session's model cannot see it.
- **Served** — `mcp serve` publishes `view_image` as its sixth read-only tool (T033). The server answers with an MCP
  `image` part; the client decides whether its model can read it. `McpServerPublishedSetTests` pins the six-name
  allow-list.

## Delegation (011)

`task` runs one self-contained instruction in a sub-agent that starts with an empty transcript and answers with
one message. The full contract, including the schema as the model sees it and the failure sentences, is
[`specs/011-subagent-task/contracts/task-tool.md`](../../011-subagent-task/contracts/task-tool.md). What a reader
of this catalogue needs is where the tool sits among the others:

- **Safety** — `Destructive` in every build, even though the default child cannot write. The child's powers are
  decided at call time, so a plan turn and a `readonly` session must refuse the delegation itself rather than rely
  on what the child would have been allowed to do. In `ask` the prompt shows the label, the iteration ceiling and
  the delegated prompt verbatim — the developer is approving an instruction, not a JSON line.
- **Registry** — the child is handed the registry this build serves, minus `task`, read at call time. `--mcp`
  tools are in it, because `AddMcp` replaces the registry after startup; delegation is not, so the depth of any
  call tree is one. A host that somehow registered the tool twice still cannot delegate from inside a child.
- **Prompt** — the child runs `PromptLibrary.SubAgentPrompt` in an `AgentSession` of its own, and the tool holds a
  refusing `IToolApprover`, so no code path inside a `task` call can raise a question while the developer is
  watching the parent turn and cannot tell which of two nested turns is asking.
- **Approval** — the child is `AllowAll` only when the parent is, and `ReadOnly` otherwise, `ask` included: a
  child that can write silently is a parent that can write silently. A refused child write is reported to the
  child, which is what lets it answer rather than stall.
- **Result** — one header and the child's final message, bounded by `MaxToolResultChars` and marked
  `IsTruncated` when it was cut. A child that finished without a word, ran out of its model calls, could not run,
  or was cancelled is a failed result with the same header and one sentence naming the outcome.
- **Effects** — every `Created`, `Modified`, `Deleted` and `CommandRun` the child reported, in order, on success
  and on failure alike. `Read` effects are dropped: the parent did not read those files, and forwarding them would
  inflate its `FilesTouched` with a claim it never made. The forwarded edit is what puts a delegated `.cs` change
  behind the parent's `VerifyGate`.
- **Not forwarded** — the child's messages, tool results and reasoning appear nowhere in the parent's transcript,
  which is the point of the tool. Its model calls and tokens appear only in the header, and `/cost` does not count
  them: there is no channel from a tool result back into session usage, and the round says so rather than
  pretending to a budget it cannot enforce.


