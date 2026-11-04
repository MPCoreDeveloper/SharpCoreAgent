# Tasks: `credentials --check`

- [x] T001 Add `CredentialEntry`, `ReadEntries` and `ParseEntries`; keep `Read`/`Parse` dictionary-compatible.
- [x] T002 Add `CliCommand.Credentials`, `--check`, command parsing and dispatch.
- [x] T003 Add `CredentialsCommand` with file:line, environment and missing provenance.
- [x] T004 Add help usage, option row and README documentation.
- [x] T005 Tests: file line, environment, missing, provider narrowing, exit codes and no secret text.
- [x] T006 Run Release build, full suite and recorded evaluation.

## Evidence

- `ReportsFileLineEnvironmentAndMissingWithoutValues`: a temporary `credentials.env` supplies `DEEPSEEK_API_KEY` on
  line 2, `XAI_API_KEY` and `OPENAI_API_KEY` come from the injected environment, and the output contains
  `file:<path>:2`, `environment` and `demo`, but neither secret value.
- `AProviderWithNoConfiguredKeyExitsOne`: `--provider deepseek` with no key returns 1 and mentions no other provider.
- `CheckIsRejectedOnEveryOtherCommand`: `chat --check` is a usage error.
- Full suite **1,642/1,642**; Release build zero warnings.

## Out of scope

Directory credential files, secret values/fingerprints, JSON output, writing/rotation, and changes to runtime credential
precedence.
