# Feature Specification: C# compiler oracle

**Feature**: `034-csharp-compiler-oracle`
**Created**: 2026-11-04
**Status**: implemented 2026-11-04

## Why this exists

F# has compiler-backed diagnostics, symbol uses and signature help. C# — the language this product is named for — does
not, so a C# edit is checked by a full `dotnet build` or by a name-based index that cannot distinguish same-named members.
This round gives C# the same three language-neutral answers through the existing `ICodeDiagnostics`, `ISymbolUses` and
`ISignatureHelp` ports. Roslyn is linked in; project XML and restored assets are read as files, with no build host.

## User stories

### US1 — Check one C# file without a build (P1)

`file_diagnostics` names the same compiler IDs, positions and messages that `dotnet build` would for that file. A clean
file and a file whose project could not be described are different answers.

### US2 — Follow the symbol the compiler bound (P1)

`uses_at` at a token returns every source use in the in-memory solution, including an in-workspace referenced project, with
the declaration marked. Same-named unrelated members and text in comments or strings are excluded.

### US3 — Inspect the call that applies here (P1)

`signature_at` at a C# invocation or object creation lists applicable overloads, marks the compiler-selected one, names
each parameter and identifies the argument under the position.

### US4 — Understand a refusal (P2)

No C# project, an unrestored package graph, an unsupported target, a computed source list or an unreadable project produces
a stable `CSH###` failure naming the next tool to call. It never produces guessed diagnostics.

## Functional requirements

- **FR-001**: Claim `.cs` only. `.csx`, Razor, generated files and other languages are excluded.
- **FR-002**: Find the nearest workspace `.csproj` whose source set includes the file. No project is `CSH001`; a file the
  selected project does not compile is `CSH002`.
- **FR-003**: Read `TargetFramework(s)`, `AssemblyName`, `OutputType`, `LangVersion`, `Nullable`, `AllowUnsafeBlocks`,
  `DefineConstants`, `ImplicitUsings`, default and explicit compile items, `ProjectReference`, restored package compile
  assets and `FrameworkReference` without running MSBuild.
- **FR-004**: Read supported properties from the nearest `Directory.Build.props`/`.targets` inside the workspace. A source
  list requiring unsupported property or conditional evaluation is `CSH002`, not a guess.
- **FR-005**: Resolve framework metadata from the installed reference pack matching the target's major. Core, ASP.NET Core
  and Windows Desktop packs are supported when requested; a missing pack is `CSH002`.
- **FR-006**: Resolve package compile assets from `obj/project.assets.json`. A package project without assets, or an asset
  missing from disk, is `CSH002`.
- **FR-007**: Compile in-workspace C# project references from source into one in-memory Roslyn solution. No build or output
  assembly load. A reference outside the workspace is `CSH002`.
- **FR-008**: Cache the solution until project/source/directory-build/assets/framework inputs change. Identical requests are
  deterministic and reuse the snapshot.
- **FR-009**: Diagnostics are compiler diagnostics for the requested file only, ordered by line then column, with 1-based
  lines and 0-based columns.
- **FR-010**: Uses are compiler-bound source locations across the solution, ordered by path, line and column; metadata-only
  locations are omitted.
- **FR-011**: Signatures support invocation and object creation, put the selected overload first, and compute the active
  parameter for positional, named and `params` arguments.
- **FR-012**: Workspace/analysis problems are `CodeFailure` values; only cancellation escapes.
- **FR-013**: C# tools are registered by default and `--no-csharp` removes the adapter and tools. `--fsharp` remains
  independent; a generic prompt rule ships whenever any adapter is registered.
- **FR-014**: Add no `Process.Start`, shell, network or write capability.

## Success criteria

- **SC-001**: A two-project fixture reports a compiler error in the root file with ID, path, line and column.
- **SC-002**: Uses on a cross-project call return declaration and use, exclude same-named/comment/string text, and repeat
  identically.
- **SC-003**: Signatures return the selected overload and active argument for positional and named arguments.
- **SC-004**: Missing/unrestored/unsupported/non-C# cases return documented `CSH###` failures without exceptions.
- **SC-005**: Focused tests pass; Release solution build and complete suite pass at zero warnings.
- **SC-006**: No existing source path gains a `Process.Start` call.

## Out of scope

Razor/Blazor generated documents, source generators, analyzers/fixes, `.csx`, VB, .NET Framework/Standard references,
`global.json` SDK selection, full MSBuild conditions/imports, refactors, formatting, solution-file-driven selection,
cross-root attachments and C# semantic indexing. Unsupported constructs fail explicitly.
