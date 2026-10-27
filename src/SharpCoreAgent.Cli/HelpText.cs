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
          sharpcoreagent run "<prompt>" [options]      one turn, then exit
          sharpcoreagent chat [options]                interactive loop
          sharpcoreagent models [--provider <name>]    list the models this build knows
          sharpcoreagent index [options]               build, inspect or repair the code index
          sharpcoreagent --help | --version

        Options
          --provider <name>        run, chat   deepseek    deepseek, grok, openai-compatible, demo
          --model <id>             run, chat   provider default
          --workspace <path>       all         current directory
          --approval <mode>        run, chat   ask         readonly, ask, allow
          --max-iterations <n>     run, chat   24
          --max-output-tokens <n>  run, chat   8000
          --max-total-tokens <n>   run, chat   250000
          --temperature <x>        run, chat   0.2
          --base-url <url>         run, chat   none        required by openai-compatible
          --session <path>         run, chat   <workspace>/.sharpcoreagent/sessions/<timestamp>.json
          --no-session             run, chat   off         do not write a transcript
          --json                   run, index  off         one JSON object per event, or per index report
          --quiet                  run, chat   off         no thinking, tool lines or summary
          --verbose                run, chat   off         stack traces for unexpected failures
          --                       run         —           everything after it is part of the prompt

        Index options
          --status                 index       off         print coverage and freshness; never writes
          --rebuild                index       off         delete every row and build from scratch
          --verify                 index       off         report drift against the workspace; never writes
          --prune                  index       off         drop rows for files that no longer exist
          --no-index               run, chat   off         no index read, write or refresh in this process
          --index-budget <ms>      run, chat   3000        refresh ceiling; 0 keeps reads and stops refresh
          --embedder <mode>        index       hashed-local  hashed-local; provider and off are not in this build
          --tier <name>            index       lean        lean; rich is not in this build

        Environment
          SHARPCOREAGENT_PROVIDER      provider used when --provider is absent
          SHARPCOREAGENT_MODEL         model used when --model is absent
          SHARPCOREAGENT_WORKSPACE     workspace used when --workspace is absent
          DEEPSEEK_API_KEY         credential for deepseek
          XAI_API_KEY, GROK_API_KEY  credential for grok, either spelling
          OPENAI_API_KEY           credential for openai-compatible; SHARPCOREAGENT_API_KEY also works

        Exit codes
          0  the turn completed and the transcript was written
          1  the turn failed: provider error, tool crash or unexpected exception
          2  the command line could not be understood
          3  a credential is missing
          4  a budget ran out, or the developer interrupted the turn
          5  the transcript could not be read or written
          6  the index could not be created, written or read

        Examples
          sharpcoreagent run "summarise what changed in src" --workspace .
          sharpcoreagent chat --provider grok --model grok-4
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
          /usage                   tokens reported per model, and the total
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
