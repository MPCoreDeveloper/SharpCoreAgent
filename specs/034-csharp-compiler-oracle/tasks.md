# Tasks: C# compiler oracle

## Setup

- [x] T001 Add Roslyn 5.9.0 centrally and create `SharpCoreAgent.Languages.CSharp` with Domain-only references.
- [x] T002 Add the project to the solution, composition root and test project.
- [x] T003 Add `--no-csharp`, default-on registration and a generic code-intelligence prompt rule.

## Project model

- [x] T004 Implement nearest-project selection and a stable, confined project/source graph.
- [x] T005 Read supported project properties, implicit/explicit compile items and directory build properties.
- [x] T006 Resolve installed reference packs, restored package compile assets and in-workspace project references.
- [x] T007 Build and cache an in-memory Roslyn solution; invalidate on graph changes.

## Language ports

- [x] T008 Implement diagnostics with compiler IDs, positions and stable failures.
- [x] T009 Implement semantic symbol lookup and cross-project uses.
- [x] T010 Implement invocation/object-creation signature help with active parameter mapping.

## Verification

- [x] T011 Add C# fixtures and focused tests for all three ports, failures, caching and cross-project behavior.
- [x] T012 Update registration, prompt, option/help and existing count assertions.
- [x] T013 Update README/contracts and record evidence.
- [x] T014 Run Release build, complete suite and evaluator baseline; verify no process/network capability was added.

## Evidence

- The two-project fixture (`App` referencing `Cart`) compiles from source in an `AdhocWorkspace`. It reports `CS0103`,
  resolves the selected `Total(decimal[], decimal)` declaration and its cross-project use, rejects the same-named
  `Ledger.Total`, and marks `decimal tax` active for a named argument.
- A source edit invalidates the cached solution and clears the diagnostic on the next request. Unsupported target
  (`net48`), unrestored package graph, no project, non-C# file and an empty position return `CSH002`, `CSH002`, `CSH001`,
  `CSH004` and `CSH003` respectively.
- C# is registered by default with 21 total tools. `--no-csharp` removes the adapter and tools; `--fsharp` adds a second
  adapter without duplicating the tool registrations. Both adapters share one `LanguageRouter`.
- Focused and integration coverage: 18 adapter/failure cases, 3 tool renderings, 4 registration cases, 3 CLI cases, 2 prompt
  cases and 3 end-to-end tool cases. Complete Release suite: **1,565/1,565**, zero warnings.
- Recorded evaluator baseline: 3/3 tasks, no semantic regressions, 52,375 recorded input / 260 output tokens.
- Source audit finds process launch only in the existing shell process registry, `dotnet_verify`'s shared registry and
  the MCP client. Roslyn reads project, source, reference and assets files; it has no network or write path.

## Out of scope

Razor/source generators, analyzers/fixes, VB, .NET Framework/Standard reference sets, full MSBuild evaluation and semantic
index extraction. They are not claimed by this slice and fail explicitly where knowledge is insufficient.
