# Quickstart: Code Evidence Index (SharpAriadne)

**Audience**: a reviewer who wants to see the feature work, in order, in about ten minutes of reading —
plus two first-index builds of roughly nine minutes each on a repository this size (§2 and §6), which
are the only slow steps and can run while you read.

## 0. Prerequisites

- .NET SDK with `net11.0` (the repository already builds with it).
- Network access to nuget.org: the six SharpCoreDB packages (`2.1.2`) are published there and
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

Expected — five labelled lines, the last one `state fresh`, and then one note per skipped file (yours
will name your own files; `.sonarqube/` is gitignored and will not exist in a fresh clone):

```
indexing D:/repos/MPCoreDeveloper/SharpAgent (lean, hashed-local/512)
  scanned     445 files · 445 added · 0 changed · 0 removed · 0 unchanged · 14 skipped
  content     10 314 passages · 4 571 symbols · 5 284 edges
  communities 329 · centers 329
  wrote       .sharpcoreagent/index in 513.5 s (69.2 MiB)
  state       fresh
  .sonarqube/out/ucfg2/cs/2_7.ucfgs: larger than 1024 KiB
  … 4 more notes
```

That block was measured on 2026-10-27 (.NET 11, Release, one developer machine) and the labels matter
more than the digits. Two things in it are easy to misread:

- `scanned` is `added + changed + removed + unchanged`, so it does **not** include the files this run
  skipped. A skipped file is remembered, which is why the next run reports it under `unchanged` and
  `skipped 0`. A file is skipped for being binary, larger than 1 MiB, or unreadable, and the notes
  under the block name it as `path: reason` (20 recorded, 10 printed).
- `wrote` names the **directory**, `.sharpcoreagent/index`, not the database file inside it.

**This is not a two-second command.** A first build hashes and chunks every file, extracts every
symbol, writes roughly 31 000 rows (files, passages, symbols, edges and their vectors) into one
SharpCoreDB file and runs community assignment over the graph: 513 s and 69 MiB for the repository
above. It is paid once. The second run — nothing changed — is `4.0 s`, and that is the path a turn
takes.

## 3. Confirm it is idempotent

```powershell
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --status
```

Expected: the second build reports `0 added · 0 changed` and every file under `unchanged` — about four
seconds instead of nine minutes, because nothing was re-read — and `--status` prints the block that
answers "what do I have":

```
index · sharpcoreagent @ D:/repos/MPCoreDeveloper/SharpAgent
  state       fresh
  tier        lean · embedder hashed-local/512 · dimensions 512
  built       2026-10-27T05:04:22Z
  files       459 indexed · 0 stale · 14 skipped · 0 orphan rows
  content     10 314 passages · 4 571 symbols · 5 284 edges
  size        69.2 MiB in .sharpcoreagent/index
  retrieval   lexical on · vector on · graph on
```

`--status` opens the index read-only and writes nothing; with no index at all it prints `state missing`
and a `hint` instead. Note that `files` now reads `459 indexed` against the first build's `445 added`:
the 14 skipped files are remembered, so they count as indexed here and as `unchanged` on the next build.

Touch one file (`Add-Content src\SharpCoreAgent.Cli\ExitCodes.cs "// x"`): `--status` counts it under
`stale`, and the next `index` reports exactly `1 changed` in the `scanned` line with `built` moved
forward.

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
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --status   # built moves forward; files … · 0 stale

# 5b: the index is never touched with --no-index
Remove-Item -Recurse -Force .sharpcoreagent\index
dotnet run --project src\SharpCoreAgent.Cli -c Release -- run "hello" --provider demo --no-index
Test-Path .sharpcoreagent\index      # False: nothing was created
```

This step leaves no index behind, which is what §6 rebuilds.

## 6. Inspect and repair

```powershell
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --rebuild
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --verify
dotnet run --project src\SharpCoreAgent.Cli -c Release -- index --status --json
```

`--rebuild` comes first because §5b deleted the index: it throws the database away and builds it again,
which costs the nine minutes of §2 and is the repair for a corrupt or newer index. On the untouched
repository it prints the same block as §2; on one where a file was edited, the same call rescues the
drift that `--verify` reports.

Expected — `--verify` compares the index against the working tree and exits 0 only when they agree:

```
verify · D:/repos/MPCoreDeveloper/SharpAgent
  files       459 in index · 459 on disk   (+0 missing from index)
  hashes      0 mismatches
  orphans     0 rows
  result      fresh
```

Delete a file behind the index's back, or edit it, and the same command reports `drift`, names what
disagrees, prints a `hint` row telling you to run `sharpcoreagent index` (or `--rebuild`) and exits `1`
— that non-zero code is the point, because a script that checks an index before a build wants it in its
control flow. `--rebuild` repairs anything, at the price of a first build (nine minutes here); a plain
`index` is enough when the database itself is healthy. `--status --json` prints the same facts as one
object, for a script that would rather parse than read.

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
| `NU1101: SharpCoreDB not found` | restore cannot reach nuget.org, or a `nuget.config` above this repository redirects the packages | this repository has one feed and a `<clear />`, so check the network or proxy, and check that no user-level or machine-level `nuget.config` wins; a developer working on SharpCoreDB itself can pass `-p:SharpCoreAgentUseLocalSharpCoreDb=true` against a sibling checkout |
| `state corrupt` | a truncated database or a newer schema | `sharpcoreagent index --rebuild` |
| `state building` in a tool result | another process holds the writer lock | wait for it, or close the other `sharpcoreagent` |
| `index could not be written` (exit 6) | `.sharpcoreagent` not writable | fix permissions or run with `--no-index` |
| `vector on` but recall is poor | the query shares no identifiers with the code, and this build's embedder is a bag of tokens, not a model of meaning | search with a name from the code (`search_code "ExitCodes"`), or use `search_text`; `--embedder off` and `--embedder provider` are refused by this build, so there is nothing to rebuild to |
