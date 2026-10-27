# Implementation plan — 003 terminal console agent

## Technical context

| Item | Value |
|---|---|
| Runtime | `net11.0`, C# 15 (`LangVersion=preview`) |
| New packages | none — ANSI is a handful of escape sequences, the diff is a string builder |
| Layering | `AnsiTheme` and the approval renderer in `Cli`; `UnifiedDiff` in `Domain.Text`; `ReplaceInFileTool` in `Infrastructure.Tools` |
| Tests | xUnit v3, real temp workspaces (`ToolTestWorkspace`), no provider and no network |
| Compatibility | every new flag is optional; every new interface member has an implementation |

`UnifiedDiff` sits in `Domain` rather than `Cli` because two layers need the same rendering: the tool
produces the preview (`Infrastructure`), the approver paints it (`Cli`). `Domain` is the only project
both may reference, and the type is pure BCL.

## Design decisions

**D1 — One palette, no raw escapes.** `AnsiTheme` is the only place that knows `\u001b`. Every renderer
takes an `AnsiTheme?` and defaults to `AnsiTheme.Plain`, so existing call sites and tests keep working and
a new renderer cannot invent its own colour. `Paint` returns the input unchanged when disabled, which
means "no colour" is not a second code path in the renderers.

**D2 — `auto` is the safe default.** `Create(choice, redirected, environment)` decides once per process:
`always` paints, `never` does not, `auto` paints only when the stream is a terminal, `NO_COLOR` is absent
and `TERM` is not `dumb`. `NO_COLOR`'s presence (even empty) is the opt-out the specification defines.
`--json` never consults the palette: `ConsoleObserver` forces `Plain` in its constructor.

**D3 — Preview is a tool capability, not a tool name.** `IToolPreview.DescribeChangeAsync` returns text
or `null`; `CodingAgent` attaches it to `ToolApprovalRequest.Preview` and swallows the failure modes a
tool is allowed to report. `ConsoleApprover` renders the preview before the question, because the answer
must be readable above the cursor.

**D4 — Fragment edits match exactly.** `replace_in_file` matches ordinally, counts occurrences before
writing, refuses zero and (without `all`) more than one, and writes through a temp file in the target
directory then renames. The preview and the write share one `ReadAsync`, so the diff the developer
approves is the text that is written.

**D5 — The diff describes a region, not an LCS table.** `UnifiedDiff` trims the shared prefix and suffix,
renders one hunk with two lines of context and stops at 60 lines with a count. That is linear in the file
size, matches what a full algorithm produces for a single edit, and is honestly labelled when the change
is large. Line endings are normalised first; a text that differs only in terminators reports no change.

**D6 — Plan mode is two enforcements of one fact.** The request omits destructive definitions, and
`GateAsync` denies one if the model names it anyway. The instruction is prepended per request
(`PromptLibrary.PlanModeInstruction`) rather than written into the session, so `/mode act` restores the
previous policy without rewriting the transcript.

**D7 — Compaction protects protocol validity.** `AgentSession.Compact(n)` keeps leading system messages,
keeps the last `n`, and refuses a window that would start with a tool result. The inserted system notice
says how many messages were dropped, so a later answer that references them is explicable.

**D8 — Aliases before identifiers.** `ModelCatalog.TryResolveAlias` runs before the bare-model path in
`/model`, so `r1` is a model choice and never a model identifier sent to a provider.


## File map

| File | Change |
|---|---|
| `src/SharpCoreAgent.Cli/AnsiTheme.cs` | new: palette, `ColorChoice`, `Create`, `Diff` |
| `src/SharpCoreAgent.Cli/ConsoleObserver.cs` | theme parameter; coloured glyphs, names, timings, summary |
| `src/SharpCoreAgent.Cli/ConsoleApprover.cs` | theme; renders `request.Preview` before the prompt |
| `src/SharpCoreAgent.Cli/CliHost.cs` | `ThemeFor(options)`; passes the theme to the approver |
| `src/SharpCoreAgent.Cli/CliOptions.cs` | `Color`, `Mode`, `--color`, `--mode`, `TryParseColor`, `TryParseMode` |
| `src/SharpCoreAgent.Cli/HelpText.cs` | new rows for both flags, `/mode`, `/compact`, `NO_COLOR` |
| `src/SharpCoreAgent.Cli/Commands/ChatCommand.cs` | prompt colours, `/mode`, `/compact`, aliases, mode on open |
| `src/SharpCoreAgent.Cli/Commands/RunCommand.cs` | observer theme; session starts in `--mode` |
| `src/SharpCoreAgent.Cli/Commands/ModelsCommand.cs` | coloured names and advisory note |
| `src/SharpCoreAgent.Domain/Agents/AgentMode.cs` | new: `Act` / `Plan` |
| `src/SharpCoreAgent.Domain/Agents/Approval.cs` | `ToolApprovalRequest.Preview` |
| `src/SharpCoreAgent.Domain/Tools/IToolPreview.cs` | new: preview contract |
| `src/SharpCoreAgent.Domain/Text/UnifiedDiff.cs` | new: hunk renderer |
| `src/SharpCoreAgent.Application/AgentSession.cs` | `Mode`, `Compact(n)` |
| `src/SharpCoreAgent.Application/CodingAgent.cs` | `MessagesFor`, `DefinitionsFor`, plan denial, preview |
| `src/SharpCoreAgent.Application/PromptLibrary.cs` | `PlanModeInstruction`; `replace_in_file` rule |
| `src/SharpCoreAgent.Application/ToolRegistry.cs` | `Tools`, so plan mode can filter by safety |
| `src/SharpCoreAgent.Infrastructure/Tools/ReplaceInFileTool.cs` | new tool + preview |
| `src/SharpCoreAgent.DependencyInjection/ServiceCollectionExtensions.cs` | registers the twelfth tool |
| `src/SharpCoreAgent.Domain/PublicAPI.Unshipped.txt` | new public surface |

## Testing strategy

- **Palette rules (8 tests).** Redirect, `NO_COLOR`, `TERM=dumb`, `always`, `never`, empty text, and the
  per-line diff codes. No console is required: `Create` takes `redirected` and an environment reader.
- **Diff arithmetic (6 tests).** Exact hunk output for a single-line change, `-0,0` for a top insertion,
  CRLF normalisation, truncation, argument validation.
- **Edit safety (7 tests).** One match, ambiguous refusal with the file untouched, `all=true`, missing
  text, path escape as a refusal, preview content, preview `null` on a miss.
- **Session hygiene (5 tests).** Fits, keeps system + window, refuses an orphan tool window, rejects a
  non-positive window, mode default.
- **Command line (6 tests + 5 alias cases).** Defaults, three colour words, plan parsing, two hint
  messages, every alias, unknown alias.

## Phases

| Phase | Scope | State |
|---|---|---|
| P0 | palette, observer, approver, help | done |
| P1 | `replace_in_file`, `UnifiedDiff`, preview wiring | done |
| P2 | `AgentMode`, plan filtering, denial, `/mode` | done |
| P3-lite | `/compact` | done |
| P4-lite | model aliases | done |
| P4-full | catalogue refresh against live provider docs, `/cost`, 401/5xx fallback | not started |
| P3-full | `--resume`, git checkpoints, `/undo` | not started |
| P5 | MCP client (opt-in) | not started |

## Rollout

Purely additive: no existing flag changed meaning, no existing output changed when the stream is
redirected or `NO_COLOR` is set, and no transcript field was removed. `--session` remains the resume
mechanism, and `--approval readonly` remains a coarser guard than `--mode plan` rather than being
replaced by it.
