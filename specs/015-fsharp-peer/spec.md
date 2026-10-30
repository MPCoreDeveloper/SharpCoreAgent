# Spec — 015 F# code intelligence

**Status**: complete — all four phases landed 2026-10-30. Phase 4's proof is in `tasks.md`: the `.fs` gate tests
fail when `".fs"` is removed from `VerifyGate.DotnetExtensions`, and the whole suite passes 1167/1167 with zero
diff on the index.

**Depends on**: `002` (landed) for the tools' neighbours and the house style of a retrieval answer; `012` (landed) for
`dotnet_verify`, which stays the authority this round defers to and never replaces.

## Why this round exists

Every tool this product ships reads bytes. `search_text` finds a name, `find_references` follows the index's typed
edges, and `dotnet_verify` asks `dotnet build` for a verdict. None of them can answer the two questions a developer
asks all day:

> Where else is *this* symbol used, the one under the cursor?
> What does this call take, here?

The index cannot answer the first honestly: it matches names, so a method and a field called `Total` on different
types are one symbol to it. A build cannot answer the second at all. Both are questions a compiler answers in
milliseconds once it has the project in memory — which is what an in-process type checker is.

F# is the language this happens for first, and that is a property of the F# toolchain rather than of F# developers:
`FSharp.Compiler.Service` is the SDK's own front end, published as a library, with a documented API for diagnostics,
symbol uses and signature help. There is no server to start, no protocol to speak and no second process to supervise —
so the round can hold the repository's rule that `Process.Start` lives in exactly two places (the shell tool and the MCP
client) while shipping real code intelligence.

## What it adds

Three language-neutral tools, one adapter, and one router between them.

| Tool | Answers | Positional |
|---|---|---|
| `file_diagnostics` | what the compiler says about one file | no — a path |
| `uses_at` | every site the compiler bound to one declaration | yes — line, column |
| `signature_at` | what a callable takes, and which argument the position is inside | yes — line, column |

The tools are language-neutral by construction: they are handed `SharpCoreAgent.Domain.Code` ports and never learn that
F# exists. `LanguageRouter` is the only type that knows an adapter list exists, and it picks one from the path's
extension alone.

## Requirements

- **FR-001** The three tools are read-only (`ToolSafety.ReadOnly`), so a plan-mode turn may call them and no approval
  prompt interrupts them.
- **FR-002** The answers come from a compiler's view of the project, not from a name match: `uses_at` excludes a
  same-named member on another type, a string that spells the name, and a comment.
- **FR-003** Nothing in the capability starts a process. Diagnostics, uses and signatures are answered in-process by a
  linked-in checker; `Process.Start` still appears only in `RunShellCommandTool` and the MCP client.
- **FR-004** A failure is a value, never an exception: `CodeFailure` carries a stable code (`FSH001`–`FSH006` from the
  adapter, `LANG001`/`LANG002` from the router), one sentence, and a hint naming a tool this repository registers.
- **FR-005** `dotnet_verify` remains the only gate: a clean `file_diagnostics` does not mark a turn verified, and the
  prompt says so (`PromptLibrary.FSharpRule`).
- **FR-006** The capability is off unless asked for (`--fsharp` / `SharpCoreAgentOptions.FSharpToolsEnabled`). Without
  it there is no checker in the process, no router and no tool to call — asserted on the container, not the tool.
- **FR-007** The prompt rule ships with the tools or not at all: `PromptLibrary.SystemPromptFor` takes the flag, and the
  shipped prompt names no tool a host may not have.
- **FR-008** The adapter is Domain-only: `SharpCoreAgent.Languages.FSharp` references `SharpCoreAgent.Domain` and
  `FSharp.Compiler.Service` and nothing else — no tools, no agent loop, no options object.
- **FR-009** Positions use the editor's convention on the way in and out: a 1-based line and a 0-based column, printed
  as `path:line:column` and adjusted by nothing, so a diagnostic can be pasted into an editor.
- **FR-010** One adapter instance per process, so the first answer's reference loading is paid once and the checker's
  parse cache is worth having.


## Success criteria

- **SC-001** `file_diagnostics` on the fixture project returns exactly one `FS0039` with the id, severity, path, line
  and column a compiler would give — asserted, not described.
- **SC-002** `uses_at` on a call in `Program.fs` returns exactly two uses: the declaration in `Cart.fs` and the call,
  each labelled.
- **SC-003** `signature_at` returns `val total: items: Item list -> decimal` with `items: Item list` as the active
  argument at the call site.
- **SC-004** All three refuse a `.cs` path with `LANG001` rather than guessing, and the adapter's own defensive
  `FSH004` is still proved by its own test.
- **SC-005** The probe's numbers hold in the product: the first question about a project costs seconds, every later
  question on the same instance costs milliseconds (phase 0 measured 3.5–5.2 s cold, 2 ms warm).
- **SC-006** `git diff --stat` over `Domain/Index`, `Infrastructure/Indexing` and `HeuristicSyntaxFactsExtractor.cs` is
  empty: this round adds a language view and does not touch the index.

## Out of scope, on purpose

- **C# and VB.** Roslyn is a bigger surface, and `HeuristicSyntaxFactsExtractor` is not the place to grow one (standing
  rule). A C# adapter is a later round with its own review of the ports.
- **Scripts.** `.fsx` is not claimed: the adapter answers about a project's compile list, and a script has none.
- **Edits, fixes and formatting.** The ports are read-only by construction, so a language service reached through them
  cannot become a second way to change a file.
- **A `dotnet` child process, MSBuild or a restore.** All three are what `dotnet_verify` is for, and adding one would
  put a third `Process.Start` in the product.
- **XML documentation at a position.** `GetToolTip` is unusable in the shipped checker (the `tokenTag` path throws or
  returns empty), so `SignatureInfo` is `(Display, Parameters, ActiveParameter)` and the round says so rather than
  shipping a field that is always empty.

## Stop gates

Work stops and asks if any of these turns out true: **G2** correctness needs a change under `Indexing/**`; **G3** the
loader needs a restore-spawning process; **G4** the prompt rule needs an `AgentSession` restructure. None was hit.
