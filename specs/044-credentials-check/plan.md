# Implementation Plan: `credentials --check`

**Feature**: `044-credentials-check`
**Status**: implemented 2026-11-04

## Parsing and dispatch

`CliCommand.Credentials` is added to the command map. `--check` sets `CliOptions.CredentialsCheck`; `Finish` rejects that
flag on every other command. The `credentials` case rejects positional arguments, JSON, model/role/fallback/session
flags, and the existing index/web/MCP/attachment/client/write/panel flags. `--provider` is allowed and narrows the list.

`Program` dispatches to `CredentialsCommand.Execute` with `Environment.GetEnvironmentVariable` and `Console.Out`; an
unreadable credential file reaches the existing `CredentialFileException` catch and exits 3.

## Provenance

`CredentialFile` now parses once into `CredentialEntry(Name, Value, Line)`:
- `ReadEntries(path)` and `ParseEntries(lines, source)` expose the entries;
- `Read(path)` and `Parse(...)` convert them to the existing dictionary, so every caller keeps its behavior.

The command reads the file entries into a dictionary keyed by name. For each provider credential it reports the first
match in this order:
1. file entry — `file:<absolute path>:<line>`;
2. non-blank environment value — `environment`;
3. `missing`.

The value is never formatted into the output.

## Exit and safety

- `1` when a provider has none of its accepted names configured; `0` otherwise.
- `demo` has no names and is always ready.
- No `Process.Start`, workspace access, model resolution or transcript write.
- The command does not import or print any secret.

## Tests

- `CredentialsCommandTests` drives the real parser and command with a temporary file and an injected environment reader;
  it asserts file:line, environment, missing, provider narrowing, exit codes and no secret text.
- `HelpTextTests` pins the option table columns; the new `--check` row follows the same three-column shape.
