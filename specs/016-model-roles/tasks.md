# Tasks — 016 model roles and the pasted key

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the next pass has a
real list rather than a memory.

## Phase 1 — a model per job

- [X] T001 `CliOptions.PlannerModel` / `ExecutorModel` / `ReviewerModel`, stored as text so a bare identifier can be
  resolved against the provider in force at the end of the command line.
- [X] T002 `--planner`, `--executor`, `--reviewer` in `ApplyOption`, with `SettingFor`/`FlagFor` so `config show`
  knows which layer a value came from, and `ParseRoleModel` rejecting a `provider:id` whose provider is unknown.
- [X] T003 `SettableNames` gains `planner`, `executor`, `reviewer`; `EnvironmentVariableFor` and `FromEnvironment`
  gain `SHARPCOREAGENT_PLANNER_MODEL`, `_EXECUTOR_MODEL` and `_REVIEWER_MODEL`.
- [X] T004 `AgentRoles` (`AgentRole`, `Name`, `Label`, `Text`, `For`, `ForMode`, `RequestModels`, `Resolve`) as the
  one place a role becomes a descriptor.
- [X] T005 `RunCommand` resolves the model for `cli.Mode`; `ChatCommand` does the same for a new session and leaves a
  resumed transcript on the model it recorded.
- [X] T006 `AgentSession.SessionModel` and `ChooseModel`, so `/model` is a decision about the conversation and a mode
  switch falls back to it; `/mode` calls `ApplyRoleModel`, which switches only when the model differs and says why.
- [X] T007 Banner gains one `roles` row when any role is named; `ConfigCommand.Value` prints all three settings.
- [X] T008 `RejectModelFlags` refuses the three flags on `mcp serve`, like every other model flag.
- [X] T009 Tests: `AgentRoleOptionTests` (nine cases: parsing, aliases, order independence, settings, environment,
  `RequestModels`, the refusal).

## Phase 2 — `/roles` and `/review`

- [X] T010 `/roles` prints the model each job runs on, and `(session model)` for the ones nobody named.
- [X] T011 `PromptLibrary.ReviewInstruction` — prompt text in the layer that owns the product's prompts.
- [X] T012 `/review [focus]` handled in the loop (it needs the agent, the observer and the cancellation split): sets
  `Approval = ReadOnly`, switches to the reviewer's model, runs the turn, and restores both in a `finally`.
- [X] T013 The turn is saved like any other, so a review survives a crash and a resumed transcript has it.
- [X] T014 Tests: `ChatRoleTests` (four cases: `/roles`, the mode switch and back, a role from the settings file, and
  `/review` restoring the approval mode after a turn that ran a read-only tool).

## Phase 3 — the pasted key (006 T016)

- [X] T015 `CredentialFile.DefaultPath` — `credentials.env` beside the settings document, reusing
  `AgentSettingsFile.PathFromEnvironment`.
- [X] T016 `CredentialFile.Upsert` — parse before write, replace the assignment in place, keep every other line,
  `.tmp` then replace, user-only modes on Unix, and refuse a value with a line break.
- [X] T017 `CredentialPrompt` — one question per missing provider, `[Y/n]`, a masked read (with a redirected-stdin
  fallback), the write, the process-environment overlay, and `credential-file` recorded in the settings document when
  the developer named no file.
- [X] T018 `CliHost.TryEnsureCredentials` takes the list of models and an optional prompt, checks every role and the
  fallback, and prints the three routes only for what is still missing.
- [X] T019 `run` and `chat` pass the prompt, with the settings path the process resolved.
- [X] T020 Tests: `CredentialPromptTests` (nine cases) and `CredentialFileUpsertTests` (six cases).

## Phase 4 — the paper

- [X] T021 `HelpText`: three option rows in the table's columns, a roles paragraph, the settings list, `/roles` and
  `/review` in the chat commands, and an example.
- [X] T022 `README.md`: the option table, the settings example, the environment list, the chat commands, the "three
  jobs, three models" paragraph and the first-run key note.
- [X] T023 `specs/001-coding-agent-core/contracts/cli.md`: the option rows, the interactive commands, the environment
  variables and the paste paragraph.
- [X] T024 `specs/000-parity-roadmap.md`: the round's row.

## Known limits, deliberately

- No key rotation, revocation or expiry: the file is a `NAME=value` document the developer edits, and rotation is a
  secret manager's job rather than a prompt's.
- No Windows ACL on the pasted file: `%APPDATA%` is per-user already, and the ACL API is a package the constitution
  does not carry.
- No per-role budgets or temperatures: a role names a model and nothing else.
