# Plan — 006 interactive start and config

## Shape

Two changes that share one seam: what a bare invocation means, and where a preference lives.

```text
sharpcoreagent [options]
        |
        +--> no command named, stdin is a terminal --> ChatCommand (+ banner)
        +--> no command named, stdin is a pipe ------> HelpText.Usage     (unchanged)
        |
        +--> "config" --> ConfigCommand --+--> AgentSettingsFile.Read/Save
                                          |
                                          v
                                  %APPDATA%\sharpcoreagent\config.json
                                          |
                                          v
                     CliOptions.Parse(settings: CliSettings?) applies it
                     under the environment and under the command line
```

## How the default is decided

`CliOptions.Parse` gains an `isInteractive` probe, defaulting to the process's own terminal
(`!Console.IsInputRedirected`). The decision is a one-liner in `Finish`, where "no command" used to
become `Help`:

```csharp
case null:
    return options with
    {
        Command = interactive ? CliCommand.Chat : CliCommand.Help,
        CommandExplicit = false,
    };
```

`CommandExplicit` exists for the same reason `ProviderExplicit` does: `chat` typed by a developer and
`chat` chosen because nothing was typed are the same command but not the same *intent*, and the
banner is only welcome in the second case. Both are session mutations of the same record, so nothing
downstream learns a new concept.

`--help` and `--version` keep returning before any of this: a flag is an instruction.

The probe is injected rather than read from `Console` inline so a test can describe a terminal it
cannot allocate. `Terminal.Interactive` is the one mutable static in the CLI, and it is mutable for
exactly that reason; `Program` reads it once per process.

## Why the settings file is an `Infrastructure` type

`AgentSettingsFile` sits in `SharpCoreAgent.Infrastructure/Configuration`, next to
`JsonFileSessionStore`, and follows its two rules: an atomic save (temp file plus move, so a crash
leaves an old file or a new one and never half of either) and a reproducible document (sorted keys,
two-space indent, UTF-8 without BOM, one trailing newline). Source-generated JSON
(`AgentSettingsJsonContext`) keeps the serializer off reflection, matching `SessionJsonContext`.

It is a *document* reader, not a settings model: `Read(path)` returns `string -> string` and knows
nothing about providers or approval modes. That split is the reason `config set` can repair a file
whose values the parser rejects — the JSON is still readable, only one value is wrong.

A missing file is not an error: it is the empty document, which is what "no file" means.

## Precedence without a provenance engine

Each setting's source is derivable from three facts the parser already has: the flag appeared in
`args` (`CommandLineSettings`), the environment variable for that name is set, or the key is in the
file. `config show` prints the first of those that holds; no per-setting bookkeeping, no second copy
of the merge logic, and no way for the printed provenance to disagree with the value, because both
come from the same parse.

`ApplySettings` therefore walks the file's keys and skips any name the command line or the
environment already decided. It applies the rest by *re-entering the option parser* with the
equivalent flag:

```csharp
Scan(["--approval", "ask"], options)   // same validation as --approval ask
```

One code path validates a value whether it arrived as a flag or as a line in a file, so
`config set approval ask` and `--approval ask` cannot drift apart. A `CliUsageException` raised
there is re-thrown as `ConfigurationFileException` naming the path and the key, because "unknown
approval mode 'sometimes'" is true but unhelpful when the developer typed it into a file.

## What the config command may do to a file it cannot apply

`config set`, `config unset` and `config path` read the *document* only, so an unusable value
(`"temperature": "warm"`) never blocks the command that fixes it. `config show`, `config get` and
every turn command read the document *and* apply it, so the same file fails them with exit 7 and a
message that names the key.

## Complexity Tracking

- **A new exit code (7).** Justified: 2 means the command line was not understood, and a script that
  retried a typo would retry a broken file forever. 3, 5 and 6 are taken by credentials, transcripts
  and the index, and each of those retry decisions is different from this one.
- **A mutable static probe (`Terminal.Interactive`).** The alternative is a test that cannot reach
  the interactive branch at all, or one that depends on whether `dotnet test` was launched from a
  terminal. Replacing it is a one-line assignment in the tests that need it.
- **The settings file lives outside the workspace.** Deliberate, and the reason it is not a
  `.env`-style discovery: a repository must not be able to change what a turn is billed to.
