# 005 — Credential file

**Status**: implemented
**Branch**: `feature/sharpcoredb-2-1-2`
**Depends on**: `001-coding-agent-core`, `003-terminal-console-agent`

## Why this feature exists

Principle VIII has always allowed credentials from "environment variables or a git-ignored local
configuration file", but only the first half was built: `EnvironmentCredentialStore` reads
`Environment.GetEnvironmentVariable` and nothing else. `.gitignore` even lists `*.env`, `.env` and
`secrets.json` — a safety net for a file the product could not read.

The gap stopped being theoretical on 2026-10-27. A key had been set with the two-argument
`[Environment]::SetEnvironmentVariable('DEEPSEEK_API_KEY', '<key>')`, which changes the *process*
scope and never reaches the registry, so every new terminal was missing it while the window that set
it looked correct. Diagnosing that cost an afternoon and produced a demo of its own: even a
correctly written User-scope value is invisible to a process that was already running, which is why
`setx` still needs a restart of the editor that spawns the agent. A key that lives in a file the
product reads at startup has neither failure mode: it survives a restart, a new shell, a different
terminal and a second machine's account, and it is one edit away from being rotated.

Two designs were rejected.

- **Read `.env` implicitly.** The agent reads repository content on purpose, and a workspace file
  that silently changes where requests are billed is exactly the kind of ambient authority principle
  III keeps out. It would also make `sharpcoreagent` behave differently depending on the directory
  it was started in, with nothing on the command line saying so.
- **Store the key encrypted.** A keychain is a platform dependency, and principle "no native
  binaries, no platform-specific code outside the shell tool" is explicit about the cost of one.

What is left is a path the developer names, once, and the existing `overrides` seam inside
`EnvironmentCredentialStore`, which already gives configuration precedence over the environment.

## User stories

### US1 — "Name the file once and stop thinking about it" (P0)

As a developer whose terminal and editor each have a different view of the environment, I want to
name a file that holds my keys, so that a run behaves the same in every window without a restart.

Acceptance:

- `--credential-file <path>` names the file, and `SHARPCOREAGENT_CREDENTIAL_FILE` supplies the same
  default; an explicit argument wins, like every other option.
- The file holds one `NAME=value` per line. Blank lines, lines starting with `#`, an optional
  `export ` prefix and a value wrapped in matching single or double quotes are all understood;
  nothing else is interpreted (no `$NAME`, no escapes).
- A key in the file wins over the same key in the process environment, because a file the developer
  named is configuration and a bare variable is ambiance. A blank value counts as unset and falls
  through to the environment, exactly as `setx NAME ""` already behaves.
- `models` reports the same `note:` line whether the key came from the file or the environment: the
  name it prints is the name the file must contain.

### US2 — "A wrong file is a sentence, not a stack trace" (P0)

As a developer who mistyped a path or pasted a line without its `=`, I want one clear sentence
naming the file and the line, before the first request is sent.

Acceptance:

- A missing, unreadable or malformed file ends the command with exit code `3` and one `error:` line.
- The message names the file and, for a malformed line, its line number. It never repeats the
  line's content, because a line that failed to parse is a line that may be the key itself.
- The file is read once, when the credential store is first resolved, not on every `Find`.

### US3 — "Nothing leaks" (P0)

As the person whose key it is, I want the file to change nothing about how a secret travels.

Acceptance:

- The values reach the `Authorization` header and nothing else: no log line, no transcript, no
  `/session` output, no tool result, no child process environment.
- `SharpCoreAgentOptions` carries the *path*, never a value, so the record stays printable.
- A credential file is never written by the product; it is read or it is absent.

## Out of scope

- Implicit discovery (`.env` in the workspace or in the home directory) — see the rejection above.
- More than one file, merging or includes.
- Reloading a changed file mid-session: a `chat` session keeps the store it started with, which is
  what makes a rotated key take effect on the next command and not in the middle of a turn.
- Encryption, keychains and OS credential managers.
- Writing the file: `sharpcoreagent` never creates one, so an empty path is always the developer's.
