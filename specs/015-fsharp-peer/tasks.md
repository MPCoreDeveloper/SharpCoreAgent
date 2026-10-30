# Tasks — 015 F# code intelligence

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the next pass has a real
list rather than a memory.

## Phase 0 — the probe, before the plan

- [X] T001 `Inspect.cs` outside the repository: reflection over `FSharp.Compiler.Service` 43.12.403 to answer four
  questions — is the API usable from C#, does `GetToolTip` work, does `GetMethods` return parameters, does anything
  need a child process. Answer: usable, tooltip unusable, `GetMethods` works, nothing spawns.
- [X] T002 Cold/warm measurement: 3.5–5.2 s cold and 2 ms warm on a real project, which is what justifies both the
  singleton and the off-by-default switch.

## Phase 1 — the domain ports

- [X] T003 Twelve files in `Domain/Code`: `ICodeDiagnostics`, `ISymbolUses`, `ISignatureHelp`, `ILanguageAdapter`, and
  `CodeDiagnostic`, `CodeDiagnosticsResult`, `CodeFailure`, `CodeSeverity`, `SignatureHelpResult`, `SignatureInfo`,
  `SymbolUse`, `SymbolUsesResult`.
- [X] T004 `PublicAPI.Unshipped.txt`: 129 lines, taken from the RS0016/RS0017 analyzer output rather than written by
  hand. `Domain` builds 0/0.
- [X] T005 The position contract, stated once per port: 1-based line, 0-based column, "any column inside the symbol".

## Phase 2 — the adapter

- [X] T006 `FsProject`, `FSharpProjectProbe`, `MsBuildFsProjectSource`: the compile list and the reference set, read from
  the `.fsproj` and from `project.assets.json` — including the object-shaped `compile` map a real restore produces.
- [X] T007 `FsCodeIntelligence`: one `FSharpChecker` with `keepAssemblyContents: true`, an options cache keyed by
  `.fsproj` timestamp, and three port implementations that never throw for a workspace problem.
- [X] T008 `FsFailures` `FSH001`–`FSH006`, each hint naming a tool this repository registers.
- [X] T009 `ToolTipTextRenderer` and `ActiveParameterFinder`, because the signature answer is built from `GetMethods`
  plus a call-site scan rather than from an API that returns both.
- [X] T010 `NameEnd`: widen a column inside a name to `colAtEndOfNames`, which is what `GetSymbolUseAtLocation` and
  `GetMethods` actually want.
- [X] T011 `GetUsesOfSymbol` resolved against the project's own use list, so the position and the answer come from the
  same view.
- [X] T012 Twenty-four tests in `SharpCoreAgent.Tests.Languages`: `FS0039` with exact id/severity/path/line/column, the
  clean file, cross-file uses, the signature with its active argument, `FSH001`/`002`/`004`, `Supports`, and ten
  `ActiveParameterFinder` cases.
- [X] T013 `git diff --stat` over `Domain/Index`, `Infrastructure/Indexing` and `HeuristicSyntaxFactsExtractor.cs` is
  empty (SC-006).

## Phase 3 — the router, the tools and the flag

- [X] T014 `LanguageRouter` (`Infrastructure/Code`): the first adapter whose `Supports` says yes answers, over all three
  ports; unclaimed extension `LANG001`, missing port `LANG002`.
- [X] T015 `CodeFailures` and `CodeFormat`: the two router codes, one failure rendering (`CODE: message` + `next: hint`),
  one set of count/severity words, one answer builder with the character budget.
- [X] T016 `FileDiagnosticsTool` (`file_diagnostics`), `UsesAtTool` (`uses_at`), `SignatureAtTool` (`signature_at`) —
  `ReadOnly`, required ports, arguments through `ToolArgumentReader`.
- [X] T017 `ToolArgumentReader.GetRequiredInt`, because a position with no line has no honest fallback.
- [X] T018 `SharpCoreAgentOptions.FSharpToolsEnabled` and `AddFSharpCodeIntelligence`: one adapter, one router under
  three ports, three tools, and nothing at all when the flag is off.
- [X] T019 `--fsharp` in `CliOptions` (`FSharp` → `FSharpToolsEnabled`) and one row in `HelpText`, because a capability
  the product's only host cannot turn on is dead code.
- [X] T020 `PromptLibrary.FSharpRule` and the fourth `SystemPromptFor` parameter; the rule ships only with the tools.
- [X] T021 Tests: `LanguageRouterTests` (6), `CodeIntelligenceToolTests` (10), `FSharpToolRegistrationTests` (4),
  `FSharpToolTests` (4, end to end through the real adapter), `FSharpOptionTests` (3), two prompt tests and the two
  gate tests of T023 — 31 new, all passing.
- [X] T022 `specs/000-parity-roadmap.md` round table, README, `contracts/tools.md` and this folder, in the same change.

## Phase 4 — the gate proof

- [X] T023 `CodingAgentTests`: `AnFSharpEditIsGatedLikeAnyOtherDotnetSource` — a turn whose only edit is `src/Cart.fs`
  ends `Completed` with `IsUnverified` set and a `Detail` that says so, after exactly `MaxVerificationNudges` nudges —
  and `AnFSharpEditIsClosedByAVerificationThatPassesAfterIt` for the other half. Mutated to prove they bite: deleting
  `".fs"` from `VerifyGate.DotnetExtensions` fails the first test at `Assert.True(report.IsUnverified)`; restoring the
  line passes 2/2. The gate was not weakened, and the proof is a failing run rather than a reading.
- [X] T024 Full suite, `dotnet test SharpCoreAgent.slnx`: **1167 passed, 0 failed, 0 skipped** (13.7 s). Builds are
  clean both ways — `Debug` and `Release`, 0 warnings, 0 errors each. Zero diff on the index, from
  `git diff --stat` and `git status --porcelain` (both empty for these paths):

  | Path | Diff |
  |---|---|
  | `src/SharpCoreAgent.Domain/Index` | none |
  | `src/SharpCoreAgent.Indexing` | none |
  | `src/SharpCoreAgent.Indexing/Extraction/HeuristicSyntaxFactsExtractor.cs` | none |

  So the round added a language without touching the index that serves the other one, which is the claim the router
  was built to make true.

## Open, and why

- **C# and VB adapters.** Deliberately out of scope: Roslyn's surface is larger and the standing rule forbids growing
  `HeuristicSyntaxFactsExtractor`. A C# adapter needs its own ports review, and this round's router is what makes it a
  registration rather than a rewrite.
- **`.fsx` scripts.** Not claimed: the adapter answers about a project's compile list and a script has none. Claiming
  them without a project would mean answering from a guessed reference set.
- **XML documentation at a position.** `GetToolTip` is unusable in the shipped checker, so `SignatureInfo` has no
  documentation field. If a later FCS release fixes the `tokenTag` path, the field arrives with its test.
- **A second project in one workspace.** `FSharpProjectProbe` picks one `.fsproj`; a workspace with several would need a
  rule for which one a path belongs to. Not needed for the workspaces this round was measured on.
