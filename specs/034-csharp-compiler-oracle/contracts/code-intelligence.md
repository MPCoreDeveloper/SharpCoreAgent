# Contract: C# code intelligence

**Feature**: `034-csharp-compiler-oracle`

## Inputs

- A `.cs` path inside the primary workspace.
- A 1-based line and 0-based column inside a name for `uses_at` and `signature_at`.
- Supported SDK-style project XML, nearest `Directory.Build.props`/`.targets`, installed targeting packs and, when the
  project has package references, `obj/project.assets.json`.

## Answers

- `file_diagnostics`: compiler errors/warnings/info for the requested syntax tree, ordered by line then column, rendered
  by the existing tool as `path · n error, n warning, n info` plus `path:line:column · severity ID · message`.
- `uses_at`: declaration and source references the compiler bound to the token, ordered by path/line/column. The display
  name is the symbol's simple name; a constructor displays its containing type. A declaration in another in-workspace
  C# project is included because that project is compiled from source in the same in-memory solution.
- `signature_at`: invocation and object-creation overloads, selected overload first, each with its parameter renderings
  and the active parameter at the position. Named arguments resolve by name; params clamps to its final parameter.

## Failures

| Code | Meaning | Hint |
|---|---|---|
| `CSH001` | No `.csproj` in the workspace | `list_roots`, `read_file` |
| `CSH002` | Project/source/reference graph cannot be described exactly | `dotnet_verify kind=build`, then retry |
| `CSH003` | Nothing nameable/callable at line and column | `find_references`, `search_code` |
| `CSH004` | Adapter was asked about a non-C# path | language-neutral alternatives |
| `CSH005` | Roslyn analysis failed unexpectedly | `dotnet_verify` |
| `CSH006` | Requested source does not exist | `list_roots`, `search_code` |

`LANG001`/`LANG002` remain the router's failures. Cancellation is the only exception that escapes.

## Limits

`.csx`, Razor/Blazor documents, source generators, analyzers/code fixes, VB, .NET Framework/.NET Standard reference
assemblies, full MSBuild condition/import evaluation and cross-root projects are outside this contract. A project that
needs them ends in `CSH002`, never guessed diagnostics.
