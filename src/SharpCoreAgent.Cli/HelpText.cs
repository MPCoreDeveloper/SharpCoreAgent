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
          sharpcoreagent setup                         the preferences, asked for one at a time
          sharpcoreagent mcp serve [options]           this workspace's index as an MCP server on stdio
          sharpcoreagent mcp config --client <name>    the block that adds it to claude, vscode or codex
          sharpcoreagent --help | --version

          With no command and a terminal on both ends, a session starts. When either stream is
          redirected there is nobody to prompt, so this usage block is printed instead.

        Options
          --provider <name>        run, chat   deepseek    deepseek, grok, openai-compatible, demo
          --model <id>             run, chat   provider default (deepseek-flash, grok-4.7)
          --fallback <p:model>     run, chat   none        replay a 401 or 5xx turn once at this model
          --workspace <path>       all         current directory
          --add-root <path>        run, chat   none        a read-only root the read tools may also read; repeatable
          --web                    run, chat   off         fetch_url may read http and https pages; the network is off otherwise
          --web-allow <host>       run, chat   none        also read this host, whose address is loopback, private or metadata; repeatable
          --mcp <command>          run, chat   none        start this MCP server and offer its tools; repeatable
          --client <name>          mcp config  —           claude, vscode or codex: whose configuration gets the block
          --approval <mode>        run, chat   ask         readonly, ask, allow; -y is shorthand for allow
          --max-iterations <n>     run, chat   400
          --max-output-tokens <n>  run, chat   8000
          --max-total-tokens <n>   run, chat   10000000    the ceiling a session starts with; /budget raises it
          --temperature <x>        run, chat   0.2
          --base-url <url>         run, chat   none        required by openai-compatible
          --credential-file <path> all         none        NAME=value keys, read first; they win over the environment
          --session <path>         run, chat   <workspace>/.sharpcoreagent/sessions/<timestamp>.json
          --no-session             run, chat   off         do not write a transcript
          --json                   run, index  off         one JSON object per event, or per index report
          --color <mode>           all         auto        auto, always, never; auto honours NO_COLOR and pipes
          --mode <mode>            run, chat   act         plan reads only and answers with a plan
          --quiet                  run, chat   off         no thinking, tool lines or summary
          --verbose                run, chat   off         stack traces, and a line as each turn starts
          --                       run         —           everything after it is part of the prompt

          An attached root is read-only. read_file, list_directory and search_text answer from it when
          given an absolute path, list_roots reports every root, and every write, delete and shell
          command still resolves against --workspace alone. A relative path always means --workspace.

          --web registers one more tool, fetch_url. It reads http and https only, sends no cookie and no
          credential, follows at most five redirects, and refuses loopback, private, link-local and
          cloud-metadata addresses unless --web-allow names the host — before the request and again when
          the socket is opened, and through no proxy, because a proxy would resolve the host itself. It
          returns text, never JavaScript, and a result says whether it was cut. Neither flag is a
          setting: both are gone with the process.

          --mcp starts one program per flag, speaks the Model Context Protocol to it over stdin and
          stdout, and offers the tools that program lists as <server>_<tool>, where <server> is the
          command's own name. The command runs through the platform shell, so npx, uvx and deno work as
          typed. A tool the server marks read-only runs without asking; everything else is approved like
          a write. The servers are started before the first turn and killed when the process exits, and
          none of this is a setting: with no --mcp there is no client in the process at all.

        Config
          sharpcoreagent config                     every setting, its value and its source
          sharpcoreagent config get <name>          one value, alone, for a script
          sharpcoreagent config set <name> <value>  write it to the settings file
          sharpcoreagent config unset <name>        remove it; the default applies again
          sharpcoreagent config path                the settings file in use
          sharpcoreagent setup                      the same file, written by answering five questions

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
          --no-refresh             mcp serve   off         answers describe the index as it is; no walk before a call
          --index-budget <ms>      run, chat   3000        refresh ceiling; 0 keeps reads and stops refresh
          --embedder <mode>        index       hashed-local  hashed-local; provider and off are not in this build
          --tier <name>            index       lean        lean; rich is not in this build

        Serve
          sharpcoreagent mcp serve [options]          the same shape the --mcp flag speaks, served
          sharpcoreagent mcp config --client <name>   the block that adds it to claude, vscode or codex
          Answers the Model Context Protocol on stdin and stdout for one workspace: the five read-only
          evidence tools — search_code, find_symbol, find_references, code_context and index_status —
          and nothing else. No key, no chat model, no network and no tool that writes or starts a
          process is in the process at all.

          Before a tool call answers, the index is brought up to date at most once every five seconds
          and never for longer than three. A refresh that cannot run — no index yet, another writer,
          a read-only checkout — is not an error: the call answers from the index that is there and the
          tool's own heading says how fresh it is. --no-refresh skips the walk entirely, for a checkout
          nobody should write to. Nothing here is a setting; both budgets belong to the capability.

          stdout carries the protocol and nothing else. The ready line and every diagnostic go to
          stderr, and the index is opened per call and released again, so a server held open for a day
          never stops 'sharpcoreagent index' in another terminal. End of input or Ctrl+C ends it at 0.

          Nothing here is a setting and nothing here needs a key, which is why wiring an agent is one
          command: 'mcp config --client claude' prints the block for .mcp.json at the repository root,
          'vscode' the one for .vscode/mcp.json, and 'codex' the [mcp_servers.sharpcoreagent] table for
          ~/.codex/config.toml. Each names this binary's absolute path and 'mcp serve', so the agent
          serves the directory it starts the server in; put --workspace in the args to pin another.
          The block is the only thing on stdout, so a redirect writes the file, and the one line
          naming that file goes to stderr.

        Chat
          /help                    every chat command, including /cost and /usage

        Environment
          SHARPCOREAGENT_PROVIDER      provider used when --provider is absent
          SHARPCOREAGENT_MODEL         model used when --model is absent
          SHARPCOREAGENT_WORKSPACE     workspace used when --workspace is absent
          SHARPCOREAGENT_ADD_ROOTS     read-only roots for run and chat, separated by the platform's path separator
          SHARPCOREAGENT_CREDENTIAL_FILE  credential file used when --credential-file is absent
          NO_COLOR                  any value disables colour, as --color never does
          DEEPSEEK_API_KEY         credential for deepseek
          XAI_API_KEY, GROK_API_KEY  credential for grok, either spelling
          OPENAI_API_KEY           credential for openai-compatible; SHARPCOREAGENT_API_KEY also works

          A credential file holds one NAME=value per line, with # comments and an optional export
          prefix; its keys win over the environment, and a blank value counts as unset.

        Exit codes
          0  the turn completed and the transcript was written, or mcp serve reached end of input
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
          sharpcoreagent chat --mcp "npx -y @modelcontextprotocol/server-filesystem ."
          sharpcoreagent mcp config --client vscode > .vscode/mcp.json
        """;

    /// <summary>Gets the commands the interactive loop understands.</summary>
    public static string ChatCommands { get; } = """
        Commands
          /model                   show the active model
          /model <id>              switch model, keeping the provider
          /model <provider>:<id>   switch provider and model
          /approval [mode]         readonly, ask or allow; bare reports the mode and what it stops asking about,
                                   allow lasts until exit; /approval forget revokes the yeses given to single tools
          /mode <mode>             plan or act; plan refuses every write and answers with a plan
          /compact [n]             drop old messages, keeping the system prompts and the last n (default 8)
          /budget [n|+n|unlimited] the session token budget; bare reports what is spent
          /continue                carry on from the last request; an empty line does the same after a pause
          /usage                   tokens reported per model, and the total
          /cost                    what the reported tokens cost, from a dated table (peak rate where tiered)
          /files                   files and commands from the last turn
          /session                 where the transcript is being written
          /roots                   the primary root and every read-only attachment, with its absolute path
          /config [verb]           show, path, set <name> <value>, or ask the questions and save
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
