# Contract — starting a session, and `config`

The part of the CLI contract this feature adds: what a bare invocation means, and the one command
that writes to disk. Everything else in `specs/001-coding-agent-core/contracts/cli.md` still holds.

## A bare invocation

```text
sharpcoreagent [options]
```

| Condition | Result | Exit |
|---|---|---|
| no command, standard input and output are terminals | an interactive session, as `chat` | 0 when the developer leaves |
| no command, either stream redirected | the usage block | 0 |
| `--help`, `--version`, `run`, `chat`, `models`, `index`, `config` | unchanged | unchanged |

"Redirected" is `Console.IsInputRedirected` or `Console.IsOutputRedirected`: a pipe, a here-document,
a file, a CI runner, or a host that replaced the console streams. The session is never started where a
developer cannot type into it or read it, so `sharpcoreagent` in a script cannot block on stdin and
cannot interleave a prompt with piped output.

The banner an implicit session prints goes to standard error, before the first prompt, and is
suppressed by `--quiet`. It is a box naming what decides a turn — the workspace, the model, the
approval mode, the agent mode and the token ceiling — over one dim line with the ways out:

```text
╭────────────────────────────────────────────────────────────────────╮
│ Ariadne 0.1.0 (SharpCoreAgent)                                     │
│ workspace  /home/me/project                                        │
│ model      deepseek:deepseek-flash                                 │
│ approval   ask · mode act · budget 250,000 tokens                  │
╰────────────────────────────────────────────────────────────────────╯
/help commands · /budget raise the session budget · Ctrl+C cancels a turn · /exit leaves
```

A `session` row follows the approval line when the transcript is being written.

The box is at most 74 columns wide and a value that does not fit loses its head, never its label.
The ceiling is the session's, so a resumed transcript shows what it starts with, and `/budget`
changes the number this line would show next.

## `config`

```text
sharpcoreagent config                          # same as `config show`
sharpcoreagent config show
sharpcoreagent config get <name>
sharpcoreagent config set <name> <value>
sharpcoreagent config unset <name>
sharpcoreagent config path
```

### Where the file is

| Order | Source |
|---|---|
| 1 | the path in `SHARPCOREAGENT_CONFIG`, when the variable is set and not blank |
| 2 | `%APPDATA%\sharpcoreagent\config.json` on Windows |
| 3 | `$XDG_CONFIG_HOME/sharpcoreagent/config.json` when the variable is set |
| 4 | `~/.config/sharpcoreagent/config.json` otherwise |

The file is a JSON object whose values are strings:

```json
{
  "approval": "readonly",
  "model": "deepseek-flash",
  "provider": "deepseek"
}
```

Keys are written in sorted order, two-space indented, UTF-8 without a byte-order mark, with one
trailing newline. A missing file is the empty document. Unknown keys are refused rather than
ignored: a setting that silently does nothing is worse than a command that says so.

### Settable names

`provider`, `model`, `base-url`, `approval`, `mode`, `temperature`, `max-iterations`,
`max-output-tokens`, `max-total-tokens`, `credential-file`. Each accepts exactly what its flag
accepts, because `config set` validates by way of the option parser; a value outside the flag's
range is refused with the same message the flag gives.

Refused by name, with a reason the developer can act on:

- `api-key`, `DEEPSEEK_API_KEY`, `XAI_API_KEY` and any other credential — the file is not a home for
  a secret. `--credential-file` and the environment are.
- every flag that describes one invocation rather than a preference: `quiet`, `json`, `color`,
  `session`, `no-session`, `verbose`, `fallback`, the index flags.

### Precedence

```text
command line  >  environment  >  settings file  >  built-in default
```

`config show` prints the layer each value came from, as `command line`, `environment`, `file` or
`default`. `config show` also prints whether the active provider's credential is configured, and
which name supplied it, without ever printing a value.

### Output

`config show` is for a person:

```text
provider        deepseek                        default
model           deepseek-flash                  file
approval        readonly                        command line
credential      set (DEEPSEEK_API_KEY)
config file     /home/me/.config/sharpcoreagent/config.json
```

`config get` and `config path` print one value and nothing else, for `$(...)` in a script. Neither
is coloured.

`config set` and `config unset` print one line naming the file they changed.

### Exit codes

| Code | When |
|---|---|
| 0 | the verb did what it said |
| 2 | the line could not be understood: an unknown verb, an unknown or non-settable name, a missing value, a value the flag would reject |
| 7 | the file could not be read, written or understood |

A file whose *values* are unusable is still repairable from the tool: `config set`, `config unset`
and `config path` read only the document, so they succeed where `config show` exits 7.
