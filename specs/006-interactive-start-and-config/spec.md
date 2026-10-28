# 006 — Start like a product: a session by default, and a place to keep settings

**Status**: implemented
**Branch**: `feature/sharpcoredb-2-1-2`
**Depends on**: `001-coding-agent-core`, `003-terminal-console-agent`, `005-credential-file`

## Why this feature exists

`sharpcoreagent` with no arguments prints the usage block and exits 0. That is a defensible default
for a Unix tool with four verbs, and it is the wrong default for the product this is: every
comparable agent — `claude`, `grok`, `aider` — opens a conversation when you type its name. The
first thing a new user does is type the name, and the first thing this build did was explain itself
and quit. The session it should have started already exists: `chat` is a complete loop with `/model`,
`/approval`, `/mode`, `/compact`, `/usage`, `/cost` and `/files`. Nothing was missing except the
entry point.

The second half of the same complaint is configuration. Everything the tool knows comes from flags,
from five `SHARPCOREAGENT_*` variables or from the provider's own key variable. A developer who
prefers `grok` over `deepseek`, or `readonly` over `ask`, has to re-type that choice every launch, or
export it in a shell profile, which is invisible to the editor that spawns the agent, to a scheduled
task, and to a second machine. `005` fixed exactly this failure for the *key*; this feature fixes it
for everything else that is a preference rather than a secret.

## What this feature does

### A session by default

- No command named, and a keyboard on standard input and a screen on standard output: the process
  runs `chat`.
- No command named, either stream redirected (a pipe, a here-document, a redirect, CI, a test host):
  the usage block, unchanged, exit 0. A REPL that cannot be typed into is not a REPL, and a bare
  `sharpcoreagent` in a script must never block on stdin or interleave its prompt with piped output.
- A command named stays what it is. `--help` stays help — the flag is an instruction, not a default.
- The session says what it is before the first prompt: product name and version, the model in use,
  the approval and agent mode, the workspace, and where the transcript goes. That is the information
  `/model` and `/session` would give one command later, given before the developer has to ask.
  `--quiet` suppresses it, exactly as it suppresses the rest of the chrome.

### A settings file

`sharpcoreagent config` manages one JSON document outside the workspace:

```text
%APPDATA%\sharpcoreagent\config.json            Windows
$XDG_CONFIG_HOME/sharpcoreagent/config.json    when the variable is set
~/.config/sharpcoreagent/config.json           otherwise
```

`SHARPCOREAGENT_CONFIG` names a different file for the whole process, which is what makes the file
testable and lets a project pin its own settings without touching the user's.

| Verb | What it does |
|---|---|
| `config` / `config show` | every setting, its effective value, and where that value came from |
| `config get <name>` | one effective value, alone, for `$(...)` in a script |
| `config set <name> <value>` | validate the value the way the flag would, then write it |
| `config unset <name>` | remove one key; the built-in default takes over again |
| `config path` | the file in use, whether or not it exists |

Settable names are the controls that describe *this developer's* defaults rather than one
invocation: `provider`, `model`, `base-url`, `approval`, `mode`, `temperature`, `max-iterations`,
`max-output-tokens`, `max-total-tokens`, `credential-file`. Everything else (`--quiet`, `--json`,
`--color`, `--session`, `--no-session`, `--verbose`, the index flags) stays a property of the run it
appears on; a persisted `--json` would be a bug waiting for a developer who pipes one command.

### Precedence, and provenance

```text
command line  >  environment  >  settings file  >  built-in default
```

This is the order the tool already had for the three settings the environment could supply
(`SHARPCOREAGENT_PROVIDER`, `_MODEL`, `_WORKSPACE`); the file slots in underneath the environment so
that an explicit `export` in a shell still wins over a file the developer forgot about. `config show`
prints the layer each value came from, because "why is it using grok" is the only question this
command is ever asked, and the answer is a word per row.

Credentials are not part of this file, in either direction: the file holds no key, `config set
api-key` is refused with a pointer to `--credential-file`, and `config show` reports whether the
provider's key is *present* without ever holding its value. `005`'s rule (the credential file wins
over the environment) is untouched, because a credential is not a setting.

## What this feature rejects, and why

- **An implicit `.env` or `sharpcoreagent.json` in the workspace.** Same argument as `005`: a file
  the agent reads because of the directory it was started in, with nothing on the command line
  saying so, is ambient authority. The settings file lives in the user's configuration directory.
- **A key in the settings file.** A preferences file gets copied, committed, pasted into a chat and
  backed up; a secret does not belong there, and `005` already gave it a home designed for it.
- **An interactive first-run prompt for the key.** Tempting — it is what `claude login` does — but
  reading a secret from a terminal and writing it to disk needs echo suppression, restricted
  permissions per platform, and a rotation story; half of that is worse than none of it. The
  missing-credential error names all three supported routes instead (`T016`).
- **A defaults file per workspace.** A repository that silently changes the model a contributor's
  turns are billed to is the failure mode this feature exists to avoid. `--workspace` and
  `SHARPCOREAGENT_WORKSPACE` already cover "run this project from anywhere".

## Exit codes

One new number, because "your settings file is broken" is neither "your command line is wrong" nor
"the transcript could not be written":

| Code | Meaning |
|---|---|
| 7 | the settings file could not be read, written or understood |

`config set`, `config unset` and `config path` keep working on a file that fails to *apply*
(`"approval": "sometimes"`), so the developer can always fix it from the tool; `config show` and
every turn command report it and exit 7.
