# Tasks — 006 interactive start and config

Legend: `[X]` implemented and covered by tests in this change; `[ ]` not started, kept here so the
next pass has a real list rather than a memory.

## Phase 1 — a session by default

- [X] T001 Add `Terminal.Interactive` (`!Console.IsInputRedirected`) as the process's probe, with the mutable field documented as the seam a test replaces.
- [X] T002 `CliOptions.Parse` takes `isInteractive`; a bare command line becomes `Chat` when the probe says a keyboard is attached and `Help` when it does not. Add `CommandExplicit` so the banner can tell "nothing typed" from "chat typed".
- [X] T003 Banner in `ChatCommand` for an implicit start: version line, model, approval, mode, workspace, transcript, and the `/help` pointer. `--quiet` still silences it, and an explicit `chat` keeps its current output.
- [X] T004 Tests: `InteractiveStartTests` (bare line, bare line with a pipe, `--help` still help, banner present, banner absent under `--quiet`) and a `ProgramTests` case that starts a real session against the demo provider through the probe.

## Phase 2 — the settings file

- [X] T005 Add `ConfigurationFileException` to `SharpCoreAgent.Domain.Errors` and list it in `PublicAPI.Unshipped.txt`.
- [X] T006 Add `Infrastructure.Configuration.AgentSettingsFile`: `ResolvePath` (`SHARPCOREAGENT_CONFIG`, `%APPDATA%`, `$XDG_CONFIG_HOME`, `~/.config`), `Read` (missing file is the empty document; malformed JSON names the path and the line) and `Save` (atomic, sorted, no BOM, one trailing newline), with `AgentSettingsJsonContext` for the serializer.
- [X] T007 `CliSettings` record (path, values, environment reader) and `CliOptions.Parse(loadSettings:)` invoked only by the commands that read settings.
- [X] T008 `ApplySettings`: skip names the command line or the environment already decided, re-enter the option parser for the rest, and re-throw a usage error as a `ConfigurationFileException` naming the path and the key.
- [X] T009 `CliCommand.Config` with the `show`, `get`, `set`, `unset` and `path` verbs, `SettableNames`, and `config set` validating through the same parser (plus `AgentOptions.Validate` for the ranges).
- [X] T010 `ConfigCommand`: the provenance table, the credential line (presence, never a value), the `api-key` refusal, and the readable messages for a file that cannot be applied.
- [X] T011 `Program`: dispatch `config`, map `ConfigurationFileException` to exit code 7, and keep `--help` ahead of everything.
- [X] T012 Rewrite the missing-credential hint to name all three routes (variable, `--credential-file`, `config set credential-file`).
- [X] T013 Tests: `ConfigCommandTests` (each verb, the provenance of each layer, a rejected value, a rejected `api-key`, an unreadable file, a file that cannot be applied) and `AgentSettingsFileTests` (round trip, sorted keys, missing file, malformed JSON, path resolution on both platforms).
- [X] T014 `ProgramTests` runs against `SHARPCOREAGENT_CONFIG` in a temporary directory, so the suite never reads the developer's own file.
- [X] T015 Documentation: `HelpText` (usage lines, the settings list, exit code 7), `specs/001-coding-agent-core/contracts/cli.md`, `specs/006-interactive-start-and-config/contracts/cli-config.md`, `README.md`, and the suite count.
- [ ] T016 An interactive first-run prompt for the key that writes a credential file with restricted permissions. (Not yet: echo suppression, per-platform permissions and rotation are three features, and the error message already names the supported routes.)
- [ ] T017 `config edit`, which opens the file in `$EDITOR` and re-validates after the editor exits. (Not yet: it needs a Windows default, and `config path` plus a real editor is one keystroke away.)
- [ ] T018 Per-provider sections (`providers.grok.model`), so `config set` can keep two backends configured without switching names. (Not yet: one flat document answers every question `config show` asks today.)
