# Tasks — 005 credential file

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the
next pass has a real list rather than a memory.

## Phase 1 — the file

- [X] T001 Add `CredentialFileException` to `SharpCoreAgent.Domain.Errors` (message names the file and the line, never the content) and list it in `PublicAPI.Unshipped.txt`.
- [X] T002 Add `Infrastructure.Credentials.CredentialFile`: `Read(path)` (`File.ReadAllLines`, IO failures wrapped) and the pure `Parse(lines, source)` implementing the grammar — comments, blanks, `export `, matching quotes, ordinal names, one assignment per line.
- [X] T003 Add `EnvironmentCredentialStore.FromFile(path)`: a null or blank path is the environment alone, otherwise the file becomes the store's `overrides`, so the existing precedence (file, then environment, blank counts as unset) is reused rather than reimplemented.
- [X] T004 Tests: `CredentialFileTests` (every grammar rule, `Read` against a temporary file, a missing path, a blank path) and `CredentialStoreTests` (precedence, blank fall-through, `GetRequired` naming only the key, `FromFile` with and without a path, a malformed file failing at construction).

## Phase 2 — the option

- [X] T005 Add `CliOptions.CredentialFile` with `--credential-file <path>` and `SHARPCOREAGENT_CREDENTIAL_FILE`, the argument winning; project it onto `SharpCoreAgentOptions.CredentialFilePath`.
- [X] T006 Register the store in the composition root from `CredentialFilePath`, so the file is read when the store is resolved and a bad file fails before the first request.
- [X] T007 Have `ModelsCommand` build its store through the same factory, so `models` and `run` agree about whether a key is configured.
- [X] T008 Move `models` inside the guarded dispatch in `Program` and add the `CredentialFileException` catch mapping to `ExitCodes.Credentials` (3).
- [X] T009 Tests: `CredentialFileOptionTests` (argument, environment default, argument beating it, a missing value) and `ProgramTests` (a missing file exits 3 naming the path, a malformed file exits 3 naming the line and not its content, `models` prints no note when the key is in the file).

## Phase 3 — documentation

- [X] T010 Add the option row, the `SHARPCOREAGENT_CREDENTIAL_FILE` line and the format note to `HelpText`, and widen exit code 3 to cover an unreadable file.
- [X] T011 Update `specs/001-coding-agent-core/contracts/cli.md` (option, parsing rules, exit code 3), `README.md` (the restart-free example) and the `.gitignore` comment that called a credential file a safety net for something unreadable.
- [ ] T012 Decide whether `--credential-file` should also accept a directory and look for a conventional name inside it. (Not yet: one path is one decision, and a directory would reintroduce the implicit discovery the spec rejects.)
- [ ] T013 Consider a `sharpcoreagent credentials --check` command that reports which keys a provider still needs and where each was found (file, line, environment) without printing a value. (Not yet: `models` already answers the first half, and the second half needs a store that remembers provenance.)
