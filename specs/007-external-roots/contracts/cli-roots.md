# Contract — attached roots

The part of the CLI and tool contract this feature adds. Everything in
`specs/001-coding-agent-core/contracts/cli.md` and `contracts/tools.md` still holds, with one tool and
one flag added.

## The flag

```text
sharpcoreagent run "why does OrderService compile here and not there" --add-root ..\Shared.Contracts
sharpcoreagent chat --add-root D:\repos\Shared.Contracts --add-root D:\repos\Proto
```

| Property | Value |
|---|---|
| repeatable | yes, and the order is kept |
| accepted on | `run`, `chat`, and a bare invocation (which is `chat`) |
| refused on | `models`, `index`, `config` — exit 2, `--add-root applies to run and chat` |
| value | a directory, absolute or relative **to the process's working directory**, exactly like `--workspace` |
| does not exist | exit 2 before any request: `error: attachment root 'D:\nope' does not exist.` |
| names a file | exit 2: `error: attachment root 'D:\a.txt' is a file, not a directory.` |
| names a duplicate | dropped silently |
| names a root nested inside another root | dropped silently: the outer root already grants it |
| names the primary root | dropped silently |

Relative to the working directory, not to `--workspace`, because the developer typed it in a shell
whose working directory is the only thing that relative path can honestly mean.

## The environment variable

```text
SHARPCOREAGENT_ADD_ROOTS=/home/me/shared:/home/me/proto      POSIX
set SHARPCOREAGENT_ADD_ROOTS=D:\repos\Shared;D:\repos\Proto  Windows
```

Split on `Path.PathSeparator` — `:` on POSIX, `;` on Windows, because a Windows absolute path already
contains a colon. Blank entries are ignored. Command-line roots come first, in the order given, then
the variable's. Both go through the same validation, so a bad entry in either is the same exit 2 with
the same message.

## The two layers that never hold a root

```text
command line  >  environment  >  settings file (never)  >  built-in default (none)
```

- `config set additional-roots …` is refused as a non-settable name, the way `api-key` is: a
  directory the agent may read is authority for one invocation, not a preference that outlives it.
- The session snapshot does not record attachments. `--session` resume starts with the roots the new
  invocation names, and nothing is inherited from the file — the transcript is evidence of what was
  read, not a standing grant.

## What one turn may touch

| Tool | Primary root | Attachment |
|---|---|---|
| `read_file`, `list_directory`, `search_text` | relative or absolute | absolute path only |
| `list_roots` | reports it | reports it |
| `write_file`, `replace_in_file`, `delete_file` | yes | **refused**, `read-only` |
| `run_shell`, `shell_output`, `shell_stop` | its working directory | unchanged: `--approval` is the boundary, not the path check |
| `search_code`, `find_symbol`, `find_references`, `code_context`, `index_status` | yes | **refused**, `not indexed` |

A relative path is always resolved against the primary root. It is never tried against an attachment,
because two repositories that both contain `src/Order.cs` must not make one argument mean whichever
root was listed first.

## The tool

```json
{
  "name": "list_roots",
  "description": "List the workspace root and every read-only attached root. A relative path is resolved against the primary root; an attachment is reached by an absolute path.",
  "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
}
```

`ToolSafety.ReadOnly`, so it needs no approval in any mode. Result, with `/` separators like every
other tool result:

```text
primary    D:/repos/App
read-only  D:/repos/Shared.Contracts
read-only  D:/repos/Proto
```

and with nothing attached:

```text
primary    D:/repos/App
```

This is the one tool result that prints an absolute path, because saying which absolute paths are
legal is its whole job.

## The banner row

```text
╭────────────────────────────────────────────────────────────────────╮
│ Ariadne 0.1.0 (SharpCoreAgent)                                     │
│ workspace  /home/me/project                                        │
│ roots      +1 read-only · /home/me/shared                          │
│ model      deepseek:deepseek-flash                                 │
│ approval   ask · mode act · budget 250,000 tokens                  │
╰────────────────────────────────────────────────────────────────────╯
```

The row appears only when at least one attachment exists, so a session without them prints the banner
006 documents byte for byte. A value that does not fit loses its head, never its label, by the rule the
other rows already follow.

## `/roots`

```text
/roots
primary    /home/me/project
read-only  /home/me/shared
```

Native separators here, because the line is for the developer's shell rather than for the model.

## What a refusal reads like

A write into an attachment:

```text
refused: path 'D:\repos\Shared.Contracts\src\Order.cs' resolves outside the workspace root
'D:\repos\App'. 'D:\repos\Shared.Contracts' is a read-only attachment; writes stay in the primary root.
```

A read outside every root:

```text
refused: path 'C:\Windows\System32\drivers\etc\hosts' is outside the workspace root 'D:\repos\App'
and its 2 read-only attachments. list_roots names the roots a path may use.
```

An index tool asked about an attachment:

```text
refused: 'Shared.Contracts/src/Order.cs' is an attachment and is not indexed. read_file and
search_text can read it; search_code answers from the primary root only.
```

## Exit codes

| Code | When |
|---|---|
| 2 | `--add-root` names something that is not an existing directory, or appears on a command that has no workspace |
| 0 | unchanged |

No new number: a bad root is a bad command line, which is what 2 already means.

