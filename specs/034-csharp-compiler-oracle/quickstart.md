# Quickstart: C# compiler oracle

From a checkout or any workspace with an SDK-style `.csproj`:

```powershell
# C# tools are on by default.
sharpcoreagent run "check src/Program.cs and fix the first compiler error" --workspace .
```

The model can then call:

- `file_diagnostics { "path": "src/Program.cs" }`
- `uses_at { "path": "src/Program.cs", "line": 12, "column": 8 }`
- `signature_at { "path": "src/Program.cs", "line": 12, "column": 18 }`

Use `--no-csharp` for an invocation that should not load Roslyn or advertise the three tools. `--fsharp` remains
independent and adds the F# adapter behind the same tools.

The first C# question reads the project graph, target packs and restored assets and builds an in-memory solution; later
questions reuse it until a listed input changes. No process, restore or network request is started. If the project has
package references, restore it first (`dotnet_verify kind=build` does this); `CSH002` names the limitation otherwise.

Measured checks on the authoring environment:

- `CSharpCodeIntelligenceTests` proves a two-project source graph, a `CS0103`, cross-project uses, named-argument
  signature help and cache refresh after a source edit.
- `CSharpToolTests` proves the three tool renderings through `LanguageRouter`.
- `CSharpToolRegistrationTests` proves default registration, single tool set for two adapters and `--no-csharp`.
- Release build: zero warnings. Complete suite: **1,565/1,565**.
