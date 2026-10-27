using System.Globalization;
using System.Reflection;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The text <c>sharpcoreagent --help</c> prints, kept next to the parser it documents.
/// </summary>
/// <remarks>
/// The table names every option with its default, because a developer deciding whether to pass a flag
/// needs the default more than the description. The exit-code rows exist for the same reason: scripts
/// branch on those numbers, so they are documented where a person can find them without the source.
/// </remarks>
internal static class HelpText
{
    /// <summary>Gets the reminder printed after a usage error.</summary>
    public const string Hint = "Run 'sharpcoreagent --help' to see the commands and options.";

    /// <summary>Gets the usage block.</summary>
    public static string Usage { get; } = """
        sharpcoreagent — a coding agent for the terminal

        Usage
          sharpcoreagent [options]                     a session in the current directory
          sharpcoreagent run "<prompt>" [options]      one turn, then exit
          sharpcoreagent chat [options]                interactive loop
          sharpcoreagent models [--provider <name>]    list the models this build knows
          sharpcoreagent index [options]               build, inspect or repair the code index
          sharpcoreagent config [show|get|set|unset|path]
                                                       settings, and where each value comes from
          sharpcoreagent --help | --version

          With no command and a terminal on both ends, a session starts. When either stream is
          redirected there is nobody to prompt, so this usage block is printed instead.

        Options
          --provider <name>        run, chat   deepseek    deepseek, grok, openai-compatible, demo
          --model <id>             run, chat   provider default (deepseek-flash, grok-4.7)
          --fallback <p:model>     run, chat   none        replay a 401 or 5xx turn once at this model
          --workspace <path>       all         current directory
          --approval <mode>        run, chat   ask         readonly, ask, allow
          --max-iterations <n>     run, chat   24
          --max-output-tokens <n>  run, chat   8000
          --max-total-tokens <n>   run, chat   250000
          --temperature <x>        run, chat   0.2
          --base-url <url>         run, chat   none        required by openai-compatible
          --credential-file <path> all         none        NAME=value keys, read first; they win over the environment
          --session <path>         run, chat   <workspace>/.sharpcoreagent/sessions/<timestamp>.json
          --no-session             run, chat   off         do not write a transcript
          --json                   run, index  off         one JSON object per event, or per index report
          --color <mode>           all         auto        auto, always, never; auto honours NO_COLOR and pipes
          --mode <mode>            run, chat   act         plan reads only and answers with a plan
          --quiet                  run, chat   off         no thinking, tool lines or summary
          --verbose                run, chat   off         stack traces for unexpected failures
          --                       run         —           everything after it is part of the prompt

        Config
          sharpcoreagent config                     every setting, its value and its source
          sharpcoreagent config get <name>          one value, alone, for a script
          sharpcoreagent config set <name> <value>  write it to the settings file
          sharpcoreagent config unset <name>        remove it; the default applies again
          sharpcoreagent config path                the settings file in use

          Settings are provider, model, base-url, approval, mode, temperature, max-iterations,
          max-output-tokens, max-total-tokens and credential-file; each accepts what its flag
          accepts. The file is %APPDATA%\sharpcoreagent\config.json on Windows and
          $XDG_CONFIG_HOME/sharpcoreagent/config.json (or ~/.config/sharpcoreagent/config.json)
          elsewhere; SHARPCOREAGENT_CONFIG names a different one. The command line beats the
          environment, which beats the file, which beats the built-in default.

          A key never belongs in it: 'config set api-key' is refused, because that file is copied
          and committed. Use --credential-file for the key and this file for the preferences.

        Index options
          --status                 index       off         print coverage and freshness; never writes
          --rebuild                index       off         delete every row and build from scratch
          --verify                 index       off         report drift against the workspace; never writes
          --prune                  index       off         drop rows for files that no longer exist
          --no-index               run, chat   off         no index read, write or refresh in this process
          --index-budget <ms>      run, chat   3000        refresh ceiling; 0 keeps reads and stops refresh
          --embedder <mode>        index       hashed-local  hashed-local; provider and off are not in this build
          --tier <name>            index       lean        lean; rich is not in this build

        Chat
          /help                    every chat command, including /cost and /usage

        Environment
          SHARPCOREAGENT_PROVIDER      provider used when --provider is absent
          SHARPCOREAGENT_MODEL         model used when --model is absent
          SHARPCOREAGENT_WORKSPACE     workspace used when --workspace is absent
          SHARPCOREAGENT_CREDENTIAL_FILE  credential file used when --credential-file is absent
          NO_COLOR                  any value disables colour, as --color never does
          DEEPSEEK_API_KEY         credential for deepseek
          XAI_API_KEY, GROK_API_KEY  credential for grok, either spelling
          OPENAI_API_KEY           credential for openai-compatible; SHARPCOREAGENT_API_KEY also works

          A credential file holds one NAME=value per line, with # comments and an optional export
          prefix; its keys win over the environment, and a blank value counts as unset.

        Exit codes
          0  the turn completed and the transcript was written
          1  the turn failed: provider error, tool crash or unexpected exception
          2  the command line could not be understood
          3  a credential is missing, or the credential file cannot be read
          4  a budget ran out, or the developer interrupted the turn
          5  the transcript could not be read or written
          6  the index could not be created, written or read
          7  the settings file could not be read, written or understood

        Examples
          sharpcoreagent run "summarise what changed in src" --workspace .
          sharpcoreagent chat --provider grok --model grok-4.7
          sharpcoreagent models --provider demo
          sharpcoreagent index --status --json
          sharpcoreagent index --rebuild --workspace .
          sharpcoreagent run "find the TODO comments" --provider openai-compatible --base-url http://localhost:1234/v1
        """;

    /// <summary>Gets the commands the interactive loop understands.</summary>
    public static string ChatCommands { get; } = """
        Commands
          /model                   show the active model
          /model <id>              switch model, keeping the provider
          /model <provider>:<id>   switch provider and model
          /approval <mode>         readonly, ask or allow; applies to the next tool call
          /mode <mode>             plan or act; plan refuses every write and answers with a plan
          /compact [n]             drop old messages, keeping the system prompts and the last n (default 8)
          /usage                   tokens reported per model, and the total
          /cost                    what the reported tokens cost, from a dated table (peak rate where tiered)
          /files                   files and commands from the last turn
          /session                 where the transcript is being written
          /help                    this list
          /exit, /quit             leave; the transcript is saved first
        """;

    /// <summary>Gets the product version, read from the assembly's informational version.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// Gets the line <c>sharpcoreagent --version</c> prints.
    /// </summary>
    /// <remarks>
    /// The former name leads because a developer who installed it under that name is looking for it here; the current
    /// one is in parentheses so a bug report quoting the line says both. The line is a property rather than a format
    /// string in <c>Program</c> so the test that pins its shape pins what is actually printed.
    /// </remarks>
    public static string VersionLine { get; } = string.Create(CultureInfo.InvariantCulture, $"Ariadne {Version} (SharpCoreAgent)");

    private static string ReadVersion()
    {
        var assembly = typeof(HelpText).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink appends "+<commit hash>"; a developer wants the number, not the hash.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
