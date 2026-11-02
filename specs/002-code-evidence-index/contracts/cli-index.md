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
| `--status` | off — a bare `index` builds | print coverage and freshness; never writes |
| `--rebuild` | off | delete every row and rebuild from scratch |
| `--verify` | off | compare the index against the workspace and report drift; never writes |
| `--prune` | off | drop rows for files that no longer exist, and re-index nothing else |
| `--embedder <mode>` | `hashed-local` | `hashed-local`, `provider`, `off` |
| `--tier <name>` | `lean` | `lean` or `rich` |
| `--workspace <path>` | current directory | the tree to index |
| `--json` | off | one JSON object on stdout, no prose |
| `--verbose` | off | stack traces for unexpected failures, and one line per phase of the build |

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
  wrote .sharpcoreagent/index/sharpcoreagent.scdb in 6.4 s wall · 6.1 s cpu (14.2 MiB)
  state fresh
```

The `wrote` line carries two clocks over one window: the wall clock the run took, and the processor time that same
window cost, process-wide. On an ordinary build they agree. When the wall clock is more than twice the processor
time and the difference is at least a minute, one extra line appears between `wrote` and `state`:

```
  clock the wall clock is 35× the processor time — the machine was asleep or the process was never scheduled, not
        the index being slow; check the power events (Kernel-Power 506/507 under Modern Standby) before believing
        the duration
```

That line is the report of a real run: `research.md` D19's eight hours were Modern Standby, and the number that
blamed the indexer was a laptop that had been asleep since eleven minutes into the build. The `clock` line only
fires on that shape, so its presence is a diagnosis and its absence is not a claim.

With `--verbose` the report adds one line per phase of the build, between `wrote` and `state`:

```
  phases      scan                0.1 s wall ·    0.1 s cpu     0%
              load                0.0 s wall ·    0.0 s cpu     0%
              write              1 m 2 s wall ·  1 m 7 s cpu    35%
              remove              0.0 s wall ·    0.0 s cpu     0%
              edges               1.9 s wall ·    2.0 s cpu     1%
              communities      1 m 49 s wall · 1 m 49 s cpu    61%
              meta                1.8 s wall ·    1.8 s cpu     1%
              of which commit   1 m 3 s wall ·  1 m 8 s cpu
              other               3.5 s wall ·    4.0 s cpu     2%
```

That is one writing run on this repository: 712 files scanned, 26 written, 350.2 MiB of index (`research.md` D21).

Every phase is present, in the order the build runs them, including the ones that did nothing — so the lines add up
instead of leaving the reader to guess which numbers are missing. The share is of the run's own wall clock. Two
lines are not phases in the same sense:

- `of which commit` is the store's own batch commit. It runs inside `write`, `remove`, `edges` and `meta`, four times
  over, and is summed once because the question it answers — what the store charges for touching it — is one
  question. It takes no share: its seconds are already inside the lines above it.
- `other` is the run minus its phases: the wait for the writer lock, opening the store, reading the status back for
  the closing line. It is printed only when it is at least 50 ms, because below that it is rounding.

`--prune` reports no phases at all. A prune drops rows rather than attributing them, and a profile of four empty
lines would suggest a build that did nothing rather than a command that is not a build.

With `--prune` the first line says `pruning` and the counters report `N removed`, `0 changed`.

When the budget stops a turn-start refresh instead of a command, the closing line is
``state partial · 214 files not yet indexed; run `sharpcoreagent index` to finish`` and `--json` carries
`"incomplete": true`.

The command's own horizon is one hour of **processor time**, not of wall clock, for the reason the `clock` line
exists: a process that was suspended has not spent the work it was given, so a build that naps is resumed rather
than reported `partial` (`research.md`, D20). The turn-start refresh keeps a wall-clock budget — `--index-budget`
— because what waits for that one is a model.

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
`unchanged`, `elapsedMs`, `cpuMs`, `phases`, `incomplete` and `notes`. `elapsedMs` is the wall clock and `cpuMs` the
processor time over the same window; a script that compares the two can tell a slow build from a suspended
machine without reading prose. `phases` is an array in build order, one object per phase with `name`, `wallMs`,
`cpuMs` and `nested` — `nested` marks the store's commit, whose time is inside the phases above it. The array is
empty when no phase ran: for `--prune`, which is not a build, and for a build that found the writer lock held, which
reports `"incomplete": true` and leaves the last complete build readable. A script that wants the split can read
`phases` whenever `incomplete` is false. For `--verify`, `action` is `verify` and the object
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

- `index` with no flags ⇒ a build, because every other message in the product says ``run `sharpcoreagent index` ``
  when the index is missing and a command that answered `--status` instead would leave that hint wrong. The build is
  incremental, so running it twice reports `0 changed`; `--status` is how a developer *looks*.
- `--status --json` ⇒ `Status` + JSON.
- `--rebuild --verify` ⇒ `CliUsageException` naming both.
- `--index-budget -1` ⇒ `CliUsageException`: "option '--index-budget' needs a whole number of
  milliseconds, zero or greater, but got '-1'".
- `--embedder nope` ⇒ `CliUsageException` listing `hashed-local`, `provider`, `off`.
- `--embedder provider` and `--embedder off` parse and then refuse with exit 2 (`this build only produces the
  hashed-local embedder…`); `--tier rich` likewise. They are parsed first so the message can quote what was asked
  and name what this build makes, instead of silently building something else.
- `--no-index` on `index` ⇒ `CliUsageException`: "the index command always uses the index."
- A leading `ariadne` token is dropped before parsing, so `ariadne index` (a shell alias for `sharpcoreagent`)
  behaves exactly like `sharpcoreagent index`; `--version` prints `Ariadne <version> (SharpCoreAgent)`.
- Parsing never touches the disk: `CliOptions.Parse` returns values, and `IndexCommand` does the I/O.

## Not in this build

`tasks.md` names the task that closes each item, where one exists.

| Promised above | Status | Why |
|---|---|---|
| `/index`, `/index rebuild`, `/index verify`, `/index off` | absent | `/index off` would have to revoke an `ICodeIndex` that the five tools already hold; the honest version of that is a restart with `--no-index`, which is what the help text says |
| `symbols.community`, `symbols.centrality`, the `communities` line | NULL / absent | T013 (Louvain over the built edge set) is not implemented; every consumer already treats a null community as "not known" |
| `quantization` in `--status` and `--json`, `documents` in the content line | absent | `IndexStatus` carries neither: the lean build always stores `int8`, and a constant field would suggest a choice that does not exist |


