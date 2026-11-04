# Feature Specification: `credentials --check`

**Feature**: `044-credentials-check`
**Status**: implemented 2026-11-04

## Problem

A key can be present in one terminal and absent from the process that runs the agent; the existing `models` command
says only that a provider has no key. `005` T013 asks for a diagnostic that names the key each provider needs and where
the configured one was found, without printing a value.

## What ships

- `sharpcoreagent credentials --check`.
- Optional `--provider <name>` narrowing and `--credential-file <path>`.
- One row per required credential name:
  - `file:<absolute path>:<line>`
  - `environment`
  - `missing`
- Exit code 0 when every listed provider has at least one configured key; 1 when one does not; 3 for an unreadable or
  malformed credential file.
- No value is printed, logged or placed in a transcript.

## User stories

1. **The key is in the file.** The report names the file and the line that declared the key.
2. **The key is in the environment.** The report says `environment`, which is enough to diagnose a stale shell.
3. **The key is missing.** The report says `missing` and exits 1, so a setup script can branch on it.
4. **One provider.** `--provider grok` reports Grok only.
5. **A bad file.** A malformed `NAME=value` file exits 3 with the existing file/line sentence, before a turn starts.

## Functional requirements

- **FR-001**: `credentials` is a command; `--check` is accepted on it and rejected elsewhere.
- **FR-002**: The report lists every provider from `ModelCatalog.Providers` unless `--provider` narrows it.
- **FR-003**: Keys come from the named credential file first, then the environment; this is the same precedence the
  runtime store uses.
- **FR-004**: The file source includes the absolute path and the one-based assignment line.
- **FR-005**: A provider with two accepted names (Grok's `XAI_API_KEY` / `GROK_API_KEY`) lists both; one configured name
  makes the provider ready.
- **FR-006**: `demo` says `(no credential required)` and never makes the result fail.
- **FR-007**: The command reads no model, opens no workspace, starts no process, writes no file and prints no value.
- **FR-008**: `--json`, model/role/fallback/session flags and index/web/MCP/attachment/panel flags are rejected.
- **FR-009**: `CredentialFile.ReadEntries` and `ParseEntries` expose line numbers; `Read`/`Parse` keep their existing
  dictionary shape for every caller.

## Non-goals

- Reading a directory of credential files (`005` T012 remains deferred).
- Rotating, writing or deleting keys.
- Showing partial key values, fingerprints or lengths.
- A JSON output mode.
- Changing the runtime credential precedence.

## Success criteria

- A temporary file plus an environment reader produces file:line, environment and missing rows with no secret text.
- `--provider deepseek` suppresses other providers.
- A provider with no configured key exits 1; all configured exits 0.
- `--check` on `chat` is a usage error.
- Full suite, Release build and recorded evaluation remain green.
