# Contract: F# code intelligence (015)

## Seam

```text
ToolCall(path[, line, column])  ->  ITool                      (Infrastructure/Tools)
                                ->  ICodeDiagnostics | ISymbolUses | ISignatureHelp   (Domain/Code ports)
                                ->  LanguageRouter             (Infrastructure/Code)
                                ->  ILanguageAdapter.Supports(path) -> FsCodeIntelligence (Languages.FSharp)
```

A tool may not reference `SharpCoreAgent.Languages.FSharp`; the language project may not reference
`SharpCoreAgent.Infrastructure` or `SharpCoreAgent.Application`. Both directions are compile errors, not conventions.

## The three tools

| Name | Safety | Required arguments | Answer |
|---|---|---|---|
| `file_diagnostics` | `ReadOnly` | `path` | diagnostics for one file |
| `uses_at` | `ReadOnly` | `path`, `line`, `column` | compiler-bound uses of one symbol |
| `signature_at` | `ReadOnly` | `path`, `line`, `column` | overloads at a call site, active one marked |

`line` is 1-based and must be ≥ 1; `column` is 0-based and must be ≥ 0. A missing `line` or `column` is a
`ToolArgumentException` naming the argument — there is no honest default for a position.

All three report one `ToolEffect(ToolEffectKind.Read, path)` on success and none on failure: an answer is derived from
a file, and nothing was written. Answers are bounded at `TextLimits.MaxCodeIntelligenceChars` (24 KiB) with
`[truncated]` and `IsTruncated` when the budget cut them.

## Answer text

```text
<path> · <n> error(s), <n> warning(s), <n> info
  <path>:<line>:<column> · <severity> <id> · <message>
<path> · no diagnostics

<display> at <path>:<line>:<column> · <n> use(s) · <n> file(s)
  definition <path>:<line>:<column> · <display>
  use <path>:<line>:<column> · <display>

<path>:<line>:<column> · <n> signature(s) · active <k> of <n>
  <k>) <display>
     <parameter>[ ← active]
  <path>:<line>:<column> · nothing callable at this position
```

`severity` is `error`, `warning` or `info` — lowercase, and `info` is never pluralised. A failure is rendered as
`<CODE>: <message>` with `next: <hint>` on the second line, and `ToolResult.IsError` is set.

## Guarantees

- **Positions are never adjusted.** The tool prints what the adapter reported, so a line a model copies from a
  diagnostic is the line an editor jumps to.
- **A clean file is not a passing build.** `no diagnostics` says one file type-checks; only `dotnet_verify` closes the
  turn's verification gate (`VerifyGate` counts `.fs`/`.fsproj`, unchanged by this round).
- **No process.** Diagnostics, uses and signatures come from a checker linked into the process. `Process.Start` remains
  in `RunShellCommandTool` and the MCP client only.
- **No new language knowledge in the prompt.** `PromptLibrary.FSharpRule` names the three tools and the position
  convention; it ships only when `FSharpToolsEnabled` registered them.
