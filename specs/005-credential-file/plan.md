# Plan — 005 credential file

## Shape

Three small pieces, no new store type:

```text
--credential-file / SHARPCOREAGENT_CREDENTIAL_FILE
        |
        v
  CliOptions.CredentialFile  (a path, never a value)
        |
        +--> SharpCoreAgentOptions.CredentialFilePath  --> container --> ICredentialStore
        |                                                                (EnvironmentCredentialStore,
        |                                                                 file values as `overrides`)
        +--> ModelsCommand (which builds its store without a container)
```

`EnvironmentCredentialStore` already resolves in the documented order — `overrides` first, then the
environment, with a blank counting as unset. The file is the `overrides` argument, so precedence,
blank handling and `IsConfigured` need no new code and no new test of the store's own logic; the
only new question is what a *file* means.

## Reading the file

`CredentialFile` is a static class in `SharpCoreAgent.Infrastructure.Credentials`, next to the store
it feeds:

- `Read(string path)` — `File.ReadAllLines`, then `Parse`. An IO failure (missing, unreadable,
  a directory where a file was expected) becomes a `CredentialFileException` naming the path.
- `Parse(IEnumerable<string> lines, string source)` — pure, so the grammar is testable without a
  file system. Every rule is one branch:

| Input | Result |
|---|---|
| *(blank)*, `   ` | ignored |
| `# comment`, `   # indented` | ignored |
| `export DEEPSEEK_API_KEY=sk-…` | the `export ` prefix is dropped |
| `DEEPSEEK_API_KEY = "sk-…"` | name and value trimmed; matching quotes removed |
| `DEEPSEEK_API_KEY='sk-…'` | same, single quotes |
| `DEEPSEEK_API_KEY=sk-"…"` | quotes not matching at both ends are kept verbatim |
| `DEEPSEEK_API_KEY=` | kept as an empty value, which the store reads as unset |
| `DEEPSEEK_API_KEY` | error: the line has no `=` (line number, no content) |
| `=value`, `1KEY=value` | error: the line does not start with a variable name |
| the same name twice | error: the second line repeats it, naming the line and the name |

Names are matched ordinally and are case-sensitive, because `deepseek_api_key` is a different
variable from `DEEPSEEK_API_KEY` and silently accepting the difference would hide the typo the file
exists to fix. No value is ever expanded, escaped or trimmed inside quotes; a key with a `#` or a
space in it survives intact, and a `#` after a value is part of the value rather than a comment.

`CredentialFileException` lives in `SharpCoreAgent.Domain.Errors`, next to
`ModelCredentialException`: `Program` already translates that family into exit code `3`, and a file
that cannot be read is the same class of problem as a key that is not there.

## Wiring

- `SharpCoreAgentOptions.CredentialFilePath` — a path, so the record stays printable (its own
  documentation states it holds no credential).
- `ServiceCollectionExtensions` registers
  `EnvironmentCredentialStore.FromFile(options.CredentialFilePath)`, so the file is read when the
  store is resolved and a malformed file fails the command before the first request.
- `ModelsCommand` builds its store the same way from `CliOptions.CredentialFile`; it never used the
  container, and giving it a second, differently-configured store would make `models` disagree with
  `run` about whether a key is configured.
- `Program` gains one `catch (CredentialFileException)` next to the existing credential catch, and
  `models` moves inside the guarded dispatch so that a bad file reaches that catch. Nothing about
  the successful path changes.

## Docs and tests

- `HelpText` gains the option row and one line in the credentials block; the exit-code row `3` learns
  about the file. `contracts/cli.md` (001) gains the same row, because it is the contract the help
  text is measured against.
- `README` shows the one-liner that removes the restart, and `.gitignore`'s comment stops calling a
  credential file a safety net for something the product cannot read.

Tests, one per rule that would otherwise rot:

- `CredentialFileTests` — the grammar table above, plus `Read` against a temporary file and a
  missing path.
- `CredentialStoreTests` — the store reads the environment, configuration wins over it, a blank
  value falls through, and a key is never quoted in the exception's message.
- `CredentialFileOptionTests` — `--credential-file` and its environment default, the argument
  winning, a missing value being a usage error, and the path reaching `SharpCoreAgentOptions`.
- `ProgramTests` — `models` with a missing file exits `3`, and `run` with a malformed file exits `3`
  naming the line, with no provider call.

## Complexity tracking

None. No new dependency, no new project, no new abstraction: one parser, one path property, one
factory method on an existing class.
