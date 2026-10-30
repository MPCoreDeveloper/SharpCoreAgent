# Plan — 015 F# code intelligence

## Shape

One adapter, one router, three tools, one flag, one prompt rule. Nothing else in the product learns a new concept.

```text
model calls file_diagnostics(path)      uses_at(path, line, column)      signature_at(path, line, column)
                    |                            |                                  |
                    +----------------------------+----------------------------------+
                                                 |
                        FileDiagnosticsTool / UsesAtTool / SignatureAtTool
                          (Domain.Code ports only: ICodeDiagnostics, ISymbolUses,
                           ISignatureHelp — never a language)
                                                 |
                                          LanguageRouter        ILanguageAdapter.Supports(path)? first yes wins
                                                 |
                                          FsCodeIntelligence    FSharpChecker, in-process
                                                 |
                            FSharpProjectProbe -> MsBuildFsProjectSource -> FSharpProjectOptions
                                                 |
                                 ParseAndCheckFileInProject / ParseAndCheckProject
                                                 |
                            CodeDiagnosticsResult / SymbolUsesResult / SignatureHelpResult
```

## Layers, and why each one exists

**Domain (`Domain/Code`).** Twelve files, no dependency beyond the BCL: three ports (`ICodeDiagnostics`,
`ISymbolUses`, `ISignatureHelp`), one routing port (`ILanguageAdapter`), and the eight records they answer with. The
ports are read-only by construction — no `Fix`, no `Format`, no `Apply` — so a language service reached through them
cannot become a second way to change a file. `CodeFailure` carries a code, a sentence and a hint, because "no project
here" is an answer a model acts on rather than an exception a caller catches.

**Languages.FSharp.** The only assembly that names a language service. It owns the checker, the project's argument
list and the cache keyed by `.fsproj` timestamp, and it answers about the workspace it was handed. It references Domain
and `FSharp.Compiler.Service` and nothing else, which is what makes a second language an addition rather than a
rewrite.

**Infrastructure/Code.** `LanguageRouter`, plus the two codes and the formatting that only it and the tools need. The
router chooses by extension in registration order; an unclaimed extension is `LANG001`, an adapter that claims a file
without implementing the port is `LANG002`.

**Infrastructure/Tools.** The three tools: arguments in, one port call, one rendering. No path resolution, no language
branch, no caching — a tool that started deciding which language it was talking to would undo the router.

**DependencyInjection.** `AddFSharpCodeIntelligence` is the only place that names `FsCodeIntelligence`. One instance,
registered as `ILanguageAdapter` and behind `LanguageRouter`, which is in turn registered under all three ports; the
three tools are `ITool` registrations beside it. The workspace is `TryAdd`ed so an index or MCP registration that
already created one wins.


## Decisions

- **`FSharp.Compiler.Service` 43.12.403, linked in.** It matches the F# 9 compiler in the pinned SDK, so the agent's
  `FS0039` and `dotnet build`'s `FS0039` are the same id with the same message. `keepAssemblyContents: true` is the
  one non-default option that matters: it is what makes a project-wide symbol-use query possible without re-checking
  every file first.
- **Off by default (`--fsharp`).** The checker is the largest dependency in the product and the first question about a
  project spends seconds resolving references. A host that does not work in F# should pay neither cost. Registration is
  gated, not the tool: without the flag there is no `ITool` to call, the same guarantee `--web` makes.
- **A router rather than three F# tools.** `fsharp_diagnostics`, `fsharp_uses` and `fsharp_signature` would charge the
  model for knowing what language a file is written in and would double the tool surface for the next language. With
  the router, adding C# is one registration line and zero prompt changes.
- **Signatures through `GetMethods`, not `GetToolTip`.** The tooltip path in the shipped checker throws or returns
  empty for a token; `GetMethods` returns the overloads with their parameters, so the answer is plural and the active
  argument is computed from the call site by `ActiveParameterFinder`.
- **The active parameter is ours.** FCS exposes no such index, and a tool that always reported the first parameter
  would be wrong more often than right.
- **`NameEnd` widening.** `GetSymbolUseAtLocation` and `GetMethods` want `colAtEndOfNames`, not the column under the
  cursor. The adapter widens any column inside a name to that name's end, and the port contract says "any column
  inside the symbol" so the two agree.
- **`dotnet_verify` keeps the gate.** `VerifyGate` already counts `.fs` and `.fsproj` as .NET source; this round adds
  nothing to it. `file_diagnostics` is the fast half of the loop, and the prompt says a clean file is not a passing
  build.
- **Project arguments are read from the restore, not from MSBuild.** `project.assets.json` carries `compile` as an
  object (path → props) in a real restore rather than an array, and `MsBuildFsProjectSource` handles both shapes. That
  is what lets the fixture run offline: its only reference is `FSharp.Core`, which the test host already has on disk.

## The answer formats

```text
file_diagnostics
  src/Program.fs · 1 error, 0 warnings, 0 info
    src/Program.fs:8:18 · error FS0039 · The value or constructor 'Subtotal' is not defined.
  src/Cart.fs · no diagnostics

uses_at
  total at src/Program.fs:8:18 · 2 uses · 2 files
    definition src/Cart.fs:5:4 · total
    use src/Program.fs:8:18 · total

signature_at
  src/Program.fs:8:20 · 1 signature · active 1 of 1
    1) val total: items: Item list -> decimal
       items: Item list ← active
```

Three properties are deliberate. A clean file and a check that could not run are different sentences (`no diagnostics`
is success; `FSH002: …` is a failure with a `next:` line). Every position is printed as `path:line:column` and adjusted
by nothing. And the counts are spelled once, in `CodeFormat`, so the three tools cannot drift.

## Failure codes

| Code | Owner | Meaning | Hint points at |
|---|---|---|---|
| `FSH001` | adapter | no `.fsproj` in the workspace | `list_roots`, `read_file` |
| `FSH002` | adapter | project not restored / not describable | `dotnet_verify` kind=build |
| `FSH003` | adapter | nothing nameable at the position | `find_references`, `read_file` |
| `FSH004` | adapter | path is not F# source (defensive) | `find_references`, `search_code`, `dotnet_verify` |
| `FSH005` | adapter | checker failed or ran out of budget | `dotnet_verify` |
| `FSH006` | adapter | path does not exist in the workspace | `list_roots`, `search_code` |
| `LANG001` | router | no adapter claims the extension | `find_references`, `search_code`, `dotnet_verify` |
| `LANG002` | router | adapter claims the file, port not implemented | (a registration bug) |

## Cost, measured before it was built

Phase 0 probed the checker outside the product: cold 3.5–5.2 s (mostly reading reference metadata), warm 2 ms, no child
process, in-process only. Those numbers are why the switch exists and why the adapter is a singleton: a per-call
adapter would pay seconds per question and throw its cache away.
