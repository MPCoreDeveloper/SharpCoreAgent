# Quickstart: Code Evidence Index (SharpAriadne)

**Audience**: a reviewer who wants to see the feature work, in order, in about ten minutes.

## 0. Prerequisites

- .NET SDK with `net11.0` (the repository already builds with it).
- Network access to nuget.org: the six SharpCoreDB packages (`2.1.0-RC.3`) are published there and
  restore like any other dependency. Nothing else has to be checked out first.
- A provider credential only if you want a real turn; `--provider demo` needs none.

## 1. Build and test

```powershell
cd d:\repos\MPCoreDeveloper\SharpCoreAgent
dotnet build SharpCoreAgent.slnx -c Release
dotnet test tests\SharpCoreAgent.Tests\SharpCoreAgent.Tests.csproj -c Release
```

Expected: zero warnings, all tests green, including the new `tests/SharpCoreAgent.Tests/Indexing/*`.

## 2. Build the index for the repository itself

```powershell
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index
```

Expected (numbers vary):

```
indexing d:/repos/MPCoreDeveloper/SharpCoreAgent (lean, hashed-local/512)
  scanned 214 files · 214 changed · 0 unchanged · 0 skipped
  passages 1 240 · symbols 690 · edges 1 502 · 7 documents
  communities 24 · centers 24
  wrote .sharpcoreagent/index/sharpcoreagent.scdb in 2.1 s (9.4 MiB)
  state fresh
```

## 3. Confirm it is idempotent

```powershell
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --status
```

Expected: `0 changed` on the second run, and `--status` reporting `state fresh`, `0 stale`.

Touch one file (`Add-Content src\SharpCoreAgent.Cli\ExitCodes.cs "// x"`), run `index` again: exactly
`1 changed`, and the build time moves forward.

## 4. Ask the index instead of reading files

```powershell
dotnet run --project src\SharpCoreAgent.Cli -c Release -- run "which type decides the exit code for a missing credential, and who calls it?" --provider demo --approval readonly
```

With the demo provider this exercises the wiring (the tools are registered, the prompt rule is present).
To see real retrieval, use a real provider, or drive the tools directly through the test harness:

```powershell
dotnet test tests\SharpCoreAgent.Tests\SharpCoreAgent.Tests.csproj -c Release --filter "FullyQualifiedName~SearchCodeToolTests"
```

The expected model-visible answer shape:

```
found 3 blocks (asked 8) · index fresh · 0 stale · lexical+vector+graph

1) src/SharpCoreAgent.Cli/ExitCodes.cs:9-20 · class ExitCodes · sha256:4b1d0f77 · why: lexical,vector
     9│internal static class ExitCodes
    10│{
```

## 5. Prove freshness, then prove the kill switch

```powershell
# 5a: edit a file the index has seen, then look for the new text
Add-Content src\SharpCoreAgent.Cli\ExitCodes.cs "// sharpcoreagent-index-probe"
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --status   # 1 changed, 0 stale after

# 5b: the index is never touched with --no-index
Remove-Item -Recurse -Force .sharpcoreagent\index
dotnet run --project src\SharpCoreAgent.Cli -c Release -- run "hello" --provider demo --no-index
Test-Path .sharpcoreagent\index      # False: nothing was created
```

## 6. Inspect and repair

```powershell
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --verify
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --rebuild
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --status --json
```

Expected: `--verify` reports `result fresh` and exits 0; after a manual edit to the database (or a file
deleted behind its back) `--verify` reports drift and exits 1; `--rebuild` repairs it.

## What to look for in the review

- `search_code` results carry `path:start-end` and `sha256:<8 hex>` on every block — no block without a
  citation (FR-012).
- Two identical queries return the identical block list (FR-013).
- `find_references` on a symbol with callers lists the callers' files, not the callee's (FR-016).
- `code_context` names the relationship for each neighbour (`calls`, `called_by`, `implements`).
- `.sharpcoreagent/` contains only `index/` and `sessions/`, and `git status` shows nothing new tracked (the
  directory is already ignored).
- With the network unplugged, everything above still works (SC-008).

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `NU1101: SharpCoreDB not found` | the local feed is missing or the mapping is wrong | `nuget.config` must list the `sharpcoredb-local` folder source (added by task T001) and map `SharpCoreDB*` to it; check that the `.nupkg` files are in `..\SharpCoreDB\artifacts` |
| `state corrupt` | a truncated database or a newer schema | `sharpcoreagent index --rebuild` |
| `state building` in a tool result | another process holds the writer lock | wait for it, or close the other `sharpcoreagent` |
| `index could not be written` (exit 6) | `.sharpcoreagent` not writable | fix permissions or run with `--no-index` |
| `vector search on` but recall is poor | `--embedder off` was used, or the corpus is prose | rebuild with the default embedder |
