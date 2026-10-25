# Contract: CLI — `sharpcoreagent index`

**Scope**: the `index` command, the index-related options on `run` and `chat`, the slash commands, and
the exit codes. Parsing stays in `CliOptions` (pure, no I/O); execution stays in `Program` behind a
small command class.

## Command surface

```
sharpcoreagent run "<prompt>" [options]           one turn, then exit
sharpcoreagent chat [options]                     interactive loop
sharpcoreagent models [--provider <name>]         list the models this build knows
sharpcoreagent index [options]                    build, inspect or repair the code index
sharpcoreagent --help | --version
```

### `sharpcoreagent index`

| Option | Default | Meaning |
|---|---|---|
| `--status` | the default when no other flag is given | print coverage and freshness; never writes |
| `--rebuild` | off | delete every row and rebuild from scratch |
| `--verify` | off | compare the index against the workspace and report drift; never writes |
| `--prune` | off | drop rows for files that no longer exist, and re-index nothing else |
| `--embedder <mode>` | `hashed-local` | `hashed-local`, `provider`, `off` |
| `--tier <name>` | `lean` | `lean` or `rich` |
| `--workspace <path>` | current directory | the tree to index |
| `--json` | off | one JSON object on stdout, no prose |
| `--verbose` | off | stack traces for unexpected failures |

`--status`, `--verify` and `--prune` may be combined; `--rebuild` may not be combined with any of them
(error: `--rebuild cannot be combined with --status, --verify or --prune.`).

### Index options on `run` and `chat`

| Option | Default | Meaning |
|---|---|---|
| `--no-index` | off | no index read, write or refresh in this process (FR-025) |
| `--index-budget <ms>` | `3000` | wall-clock ceiling for the pre-turn refresh (FR-021); `0` disables the refresh but keeps reads |
| `--embedder <mode>` | `hashed-local` | as above; `provider` additionally requires a provider credential |
| `--tier <name>` | `lean` | as above |

`--no-index` beats `--index-budget`: the refresh is skipped entirely and every index tool answers with
the "disabled" failure.

### Slash commands (chat only)

| Command | Effect |
|---|---|
| `/index` | print the same block as `sharpcoreagent index --status` |
| `/index rebuild` | rebuild in this process, printing the report when it finishes |
| `/index verify` | run the drift check and print it |
| `/index off` | stop reading the index for the rest of the session (the tools then report "disabled") |

## Output — `--status` (default)

```
index · sharpcoreagent @ d:/repos/SharpCoreAgent
  state       fresh
  tier        lean · embedder hashed-local/512 · quantization int8
  built       2026-10-24T09:12:44Z (build 01J9Z8Q7K3)
  files       412 indexed · 0 stale · 3 skipped · 0 failed
  content     1 908 passages · 1 122 symbols · 2 640 edges · 7 documents
  size        14.2 MiB in .sharpcoreagent/index
  retrieval   lexical on · vector on · graph on
```

When there is no index:

```
index · sharpcoreagent @ d:/repos/SharpCoreAgent
  state       missing
  hint        run `sharpcoreagent index` to build it
```

This block is for a person at a terminal; the `index_status` tool prints the compact three-line form in
`contracts/retrieval-tools.md`, because a model reading three lines of coverage is cheaper than reading
eleven.

## Output — a build (`sharpcoreagent index`, `--rebuild`, `--prune`)

```
indexing d:/repos/SharpCoreAgent (lean, hashed-local/512)
  scanned 1 240 files · 412 changed · 828 unchanged · 3 skipped
  passages 1 908 · symbols 1 122 · edges 2 640 · 7 documents
  communities 38 · centers 38
  wrote .sharpcoreagent/index/sharpcoreagent.scdb in 6.4 s (14.2 MiB)
  state fresh
```

With `--prune` the first line says `pruning` and the counters report `N removed`, `0 changed`.

When the budget stops a turn-start refresh instead of a command, the closing line is
``state partial · 214 files not yet indexed; run `sharpcoreagent index` to finish`` and `--json` carries
`"incomplete": true`.

## Output — `--verify`

```
verify · d:/repos/SharpCoreAgent
  files   412 in index · 414 on disk   (+2 missing from index)
  hashes  1 mismatch (src/SharpCoreAgent.Cli/Program.cs)
  orphans 0 passages · 0 symbols · 0 edges
  result  drift
```
`result fresh` when nothing drifted. `--verify` never repairs; the hint line says
``run `sharpcoreagent index` (or `--rebuild`) to repair``, because a repair is a write and the developer asked
for a check.

## Output — `--json`

One object, stable key order, no trailing prose:

```json
{"command":"index","action":"status","workspace":"d:/repos/SharpCoreAgent","state":"fresh","tier":"lean","embedder":"hashed-local/512","dimensions":512,"quantization":"int8","files":412,"stale":0,"skipped":3,"passages":1908,"symbols":1122,"edges":2640,"docs":7,"communities":38,"sizeBytes":14889779,"builtUtc":"2026-10-24T09:12:44Z","buildId":"01J9Z8Q7K3","incomplete":false,"notes":[]}
```

For a build, `action` is `build`/`rebuild`/`prune` and the object adds `added`, `changed`, `removed`,
`unchanged`, `elapsedMs`, `incomplete` and `notes`. For `--verify`, `action` is `verify` and the object
adds `healthy`, `filesMissingFromIndex`, `filesMissingOnDisk`, `hashMismatches`, `orphans` and
`details`.

## Exit codes (the existing set, plus one)

| Code | When |
|---|---|
| 0 | the command did what was asked: a build completed, `--status` printed, `--verify` found no drift |
| 1 | `--verify` found drift; a build failed part-way and left the index `partial`; unexpected exception |
| 2 | the command line could not be understood (`--rebuild --status`, an unknown `--embedder`, a negative budget) |
| 3 | `--embedder provider` was requested and the credential is missing |
| 4 | the developer interrupted the build (Ctrl+C), or another process held the lock past the wait |
| 6 | the index could not be created or written: `.sharpcoreagent` is not writable, the disk is full, the database is corrupt and `--rebuild` was not passed |

Code 6 is new (`ExitCodes.IndexUnavailable`) and is documented in `HelpText` alongside the other five,
because scripts branch on it.

## Behaviour on `run` and `chat`

1. Before the first model call: `EnsureFreshAsync(budget)`. A missing index is built; a stale one is
   refreshed; a fresh one costs one metadata read. No output when `--quiet`; otherwise one line:
   `index · fresh (412 files, 1 908 passages, 0.4 s)` or `index · partial (214 files pending)`.
2. Any file written or deleted by `write_file`/`delete_file` during a turn marks itself dirty; the refresh
   happens before the *next* turn (FR-022).
3. With `--no-index`: no line is printed, no directory is created, and the five index tools answer with
   the "disabled" failure.
4. The transcript records the refresh as one `index` event when `--json` is used, so a harness can see
   what coverage the model had.

## Parsing rules (asserted by `CliOptionsTests`)

- `index` with no flags ⇒ `Status`.
- `--status --json` ⇒ `Status` + JSON.
- `--rebuild --verify` ⇒ `CliUsageException` naming both.
- `--index-budget -1` ⇒ `CliUsageException`: "option '--index-budget' needs a whole number of
  milliseconds, zero or greater, but got '-1'".
- `--embedder nope` ⇒ `CliUsageException` listing `hashed-local`, `provider`, `off`.
- `--no-index` on `index` ⇒ `CliUsageException`: "the index command always uses the index."
- A leading `ariadne` token is dropped before parsing, so `ariadne index` (a shell alias for `sharpcoreagent`)
  behaves exactly like `sharpcoreagent index`; `--version` prints `Ariadne <version> (SharpCoreAgent)`.
- Parsing never touches the disk: `CliOptions.Parse` returns values, and `IndexCommand` does the I/O.

