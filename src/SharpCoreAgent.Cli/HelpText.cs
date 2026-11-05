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
          sharpcoreagent run "<prompt>" --image <path>  attach a workspace picture to the prompt
          sharpcoreagent run "<prompt>" --image -       attach a picture piped or pasted into stdin
          sharpcoreagent chat [options]                interactive loop
          sharpcoreagent models [--provider <name>]    list the models this build knows
          sharpcoreagent credentials --check           which provider keys are configured, and where
          sharpcoreagent index [options]               build, inspect or repair the code index
          sharpcoreagent config [show|get|set|unset|path]
                                                       settings, and where each value comes from
          sharpcoreagent setup                         the preferences, asked for one at a time
          sharpcoreagent mcp serve [options]           this workspace's index as an MCP server on stdio
          sharpcoreagent mcp config --client <name>    the block that adds it to claude, vscode, visualstudio or codex
          sharpcoreagent mcp bench [options]           the twelve fixture questions, measured against the source
          sharpcoreagent ui --serve [options]          this workspace's index as a page on 127.0.0.1
          sharpcoreagent ide agent [--write]           the agent profile that puts the index in an editor's picker
          sharpcoreagent ide prompt [--write]          the reusable prompt for .github/prompts
          sharpcoreagent ide instructions [--write]    the repository-wide instructions for .github/copilot-instructions.md
          sharpcoreagent ide skill [--write]           the agent skill for .github/skills/sharpcoreagent/SKILL.md
          sharpcoreagent ide tasks [--write]           the task that rebuilds the index, in .vscode/tasks.json
          sharpcoreagent --help | --version

          With no command and a terminal on both ends, a session starts. When either stream is
          redirected there is nobody to prompt, so this usage block is printed instead.

        Options
          --provider <name>        run, chat   deepseek    deepseek, grok, openai-compatible, demo
          --model <id>             run, chat   provider default (deepseek-flash, grok-4.7)
          --planner <model>        run, chat   session     the model a plan turn runs on; a bare id keeps --provider
          --executor <model>       run, chat   session     the model an act turn runs on, and /model's starting point
          --reviewer <model>       run, chat   session     the model /review sends its read-only turn to
          --fallback <p:model>     run, chat   none        replay a 401 or 5xx turn once at this model
          --workspace <path>       all         current directory
          --add-root <path>        run, chat   none        a root the read tools may also read; writable under --approval allow; repeatable
          --web                    run, chat   off         fetch_url may read http and https pages; the network is off otherwise
          --image <path|->         run         none        attach a PNG, JPEG, GIF or WebP picture to the prompt; repeatable, '-' reads stdin
          --fsharp                 run, chat   off         also let file_diagnostics, uses_at and signature_at ask the F# type checker
          --no-csharp              run, chat   C# on       remove the C# compiler adapter and its three code-intelligence tools
          --no-git                 run, chat   Git on      remove the read-only git_status and git_diff evidence tools
          --auto-verify            run, chat   off         run the verifier once automatically before asking the model to verify
          --no-verify              run, chat   gate on     do not require dotnet_verify after a .NET edit for this run
          --web-allow <host>       run, chat   none        also read this host, whose address is loopback, private or metadata; repeatable
          --mcp <command>          run, chat   none        start this MCP server and offer its tools; repeatable
          --mcp-url <url>          run, chat   none        talk streamable HTTP to this MCP endpoint; https, or http on loopback; repeatable
          --mcp-sse-url <url>      run, chat   none        talk legacy HTTP+SSE to this MCP endpoint; https, or http on loopback; repeatable
          --mcp-sampling <policy>  run, chat   deny        deny, first or unrestricted; first answers one server sampling request per connection
          --browser                run, chat   off         start the Playwright MCP server with Edge (Windows) or Chromium; adds npx_browser_* tools
          --client <name>          mcp config  —           claude, vscode, visualstudio or codex: whose configuration gets the block
          --write                  mcp config  off         write the block into that file instead of printing it
          --approval <mode>        run, chat   ask         readonly, ask, allow; -y is shorthand for allow
          --max-iterations <n>     run, chat   400
          --max-output-tokens <n>  run, chat   8000
          --max-total-tokens <n>   run, chat   10000000    the ceiling a session starts with; /budget raises it
          --compact-at <fraction>  run, chat   0.8         compact before the estimated prompt exceeds this fraction of the model window; 0 disables
          --temperature <x>        run, chat   0.2
          --base-url <url>         run, chat   none        required by openai-compatible
          --credential-file <path> all         none        NAME=value keys, read first; they win over the environment
          --session <path>         run, chat   <workspace>/.sharpcoreagent/sessions/<timestamp>.json
          --resume [path]          chat        none        list saved transcripts, or open the one named
          --no-session             run, chat   off         do not write a transcript
          --json                   run, index  off         one JSON object per event, or per index report
          --check                  credentials —          report the provider keys and their provenance; never the values
          --color <mode>           all         auto        auto, always, never; auto honours NO_COLOR and pipes
          --mode <mode>            run, chat   act         plan reads only and answers with a plan
          --quiet                  run, chat   off         no thinking, tool lines or summary
          --verbose                run, chat   off         stack traces, and a line as each turn starts
          --                       run         —           everything after it is part of the prompt

          A role is the model one job runs on: --planner for a plan turn, --executor for an act turn, and the
          reviewer (--reviewer) for /review. A role nobody named runs on the session model, so naming one is
          the only thing that changes; a bare identifier keeps --provider and provider:id names both. The
          same three names are settings, and SHARPCOREAGENT_PLANNER_MODEL, SHARPCOREAGENT_EXECUTOR_MODEL and
          SHARPCOREAGENT_REVIEWER_MODEL supply them from the environment. Every named role's provider is
          checked for a key before the first turn, and a missing one is offered as a paste rather than a
          restart.

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
          a write. A screenshot a server returns is written to .sharpcoreagent/artifacts/ and the answer
          names the file and its size, so the page the developer asked about is one they can open.
          The servers are started before the first turn and killed when the process exits, and none of
          this is a setting: with no --mcp there is no client in the process at all. A server that exits
          later is restarted on the next call, at most three times, with a fresh handshake; the call that
          saw the failure is not repeated. Search is not built in: a server, such as
          @brave/brave-search-mcp-server with BRAVE_API_KEY set, is what the flag
          is for.
          --mcp-url talks streamable HTTP to one explicit endpoint instead of starting a child: https, or
          http only for a loopback host. The endpoint owns its authentication; this client sends no OAuth
          flow, follows no redirect and accepts no certificate it would not accept elsewhere. Its tools,
          resources and prompts arrive under the same <server>_<tool> names as a stdio server.


          --mcp-sse-url is the older remote handshake: it opens a GET event stream, reads the POST endpoint
          the server names, and keeps answers on that stream. It has the same https/loopback rule, redirect
          refusal and tool naming as --mcp-url; use it only for a server that has not moved to streamable
          HTTP.



          --mcp-sampling decides whether a server may ask the session's model to sample: deny (the
          default) answers -32601, first answers one request per connection, and unrestricted answers
          within 10 calls and 100000 input tokens per minute, with 25000 output tokens per call. Only
          text content is accepted, and the current model answers, so /model remains the developer's
          choice. A server that advertises resources or prompts gains one read-only bridge for each:
          <server>_resources lists its catalog with no argument and reads one uri; <server>_prompts
          lists names and required arguments and renders one with name plus an arguments object. Binary
          resources are described by type and size, never written.

        Credentials
          sharpcoreagent credentials --check          each provider's key, and the file:line or environment
          sharpcoreagent credentials --check --provider grok
                                                       narrow the report to one provider

        Config
          sharpcoreagent config                     every setting, its value and its source
          sharpcoreagent config get <name>          one value, alone, for a script
          sharpcoreagent config set <name> <value>  write it to the settings file
          sharpcoreagent config unset <name>        remove it; the default applies again
          sharpcoreagent config path                the settings file in use
          sharpcoreagent setup                      the same file, written by answering five questions

          Settings are provider, model, planner, executor, reviewer, base-url, approval, mode, temperature,
          max-iterations, max-output-tokens, max-total-tokens and credential-file; each accepts what its
          flag accepts. Provider sections add providers.&lt;name&gt;.model and providers.&lt;name&gt;.base-url,
          which apply only when that provider is selected. The file is %APPDATA%\sharpcoreagent\config.json on Windows and
          $XDG_CONFIG_HOME/sharpcoreagent/config.json (or ~/.config/sharpcoreagent/config.json)
          elsewhere; SHARPCOREAGENT_CONFIG names a different one. The command line beats the
          environment, which beats the file, which beats the built-in default.

          A key never belongs in it: 'config set api-key' is refused, because that file is copied
          and committed. Use --credential-file for the key and this file for the preferences. With no
          key at all and a terminal on both ends, the first run offers to take one: it is written to
          credentials.env beside this file, never echoed, and named here as credential-file so the next
          run reads it. On other streams nothing is asked and the three routes above are printed.

        Index options
          --status                 index       off         print coverage and freshness; never writes
          --rebuild                index       off         empty every table, build from scratch, then compact
          --verify                 index       off         report drift against the workspace; never writes
          --prune                  index       off         drop rows for files that no longer exist
          --compact                index       off         give back the pages the index file no longer uses
          --no-index               run, chat   off         no index read, write or refresh in this process
          --no-refresh             mcp serve   off         answers describe the index as it is; no walk before a call
          --index-budget <ms>      run, chat   3000        refresh ceiling; 0 keeps reads and stops refresh
          --embedder <mode>        index       hashed-local  hashed-local; provider and off are not in this build
          --tier <name>            index       lean        lean; rich is not in this build

        Serve
          sharpcoreagent mcp serve [options]          the same shape the --mcp flag speaks, served
          sharpcoreagent mcp config --client <name>   the block that adds it to claude, vscode, visualstudio or codex
          sharpcoreagent mcp bench                    the twelve fixture questions, asked twice and measured
          Answers the Model Context Protocol on stdin and stdout for one workspace: the six read-only
          evidence tools — search_code, find_symbol, find_references, code_context, index_status and
          view_image — and nothing else. No key, no chat model, no network and no tool that writes or starts
          a process is in the process at all. A picture is returned as an image part; the client decides
          whether its model can read it.

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
          'visualstudio' the one Visual Studio reads from that same .mcp.json, 'vscode' the one for
          .vscode/mcp.json, and 'codex' the [mcp_servers.sharpcoreagent] table for ~/.codex/config.toml.
          Each names this binary's absolute path and 'mcp serve', so the agent serves the directory it
          starts the server in; put --workspace in the args to pin another. The block is the only thing
          on stdout, so a redirect writes the file, and the one line naming that file goes to stderr.

          --write merges the block into that file instead of printing it, because a redirect truncates:
          the file keeps every key it had, the client's object keeps every other server, and this entry
          keeps the env and cwd somebody added to it. An entry that already matches is left alone and
          said so. A file that is not JSON, or whose 'servers' is not an object, exits 7 with the file
          untouched. Run it once per client and one committed .mcp.json carries both keys — mcpServers
          for Claude Code, servers for Visual Studio — with neither client losing the other's.

          'mcp bench' asks the twelve questions in specs/013-evidence-mcp-server/bench/queries.txt
          twice — in process and over stdio — and prints, for each answer, the characters it spent
          against the characters of the whole files it cites, plus the p95 of the call. --verbose
          adds both durations per query, which is how a slow run is attributed to the hop or to
          the call. It needs a built index and says so otherwise. A query whose two answers differ
          is a failure rather than a row: the bench is where 'the server adds nothing' is a
          number and not a claim.

        Panel
          sharpcoreagent ui --serve [options]         the index as a page, until Ctrl+C
          --serve                  ui          —           serve the panel; there is no other ui action
          --port <n>               ui          0           the port to bind; 0 picks a free one
          --refresh                ui          off         walk the index before each answer, inside --index-budget
          --json                   ui          off         the object naming the URL, then the panel keeps serving

          GET / is the page and GET /api/status.json is the object 'index --status --json' prints — the same
          bytes, built by the same code — and neither is cached, because a cached page would show numbers
          that are no longer the index's. Nothing else is answered: any other path, any method but GET, and
          any Host header that is not 127.0.0.1:<port> gets 404 or 403 with an empty body, because a
          loopback listener that answers any Host is reachable from a hostile page by DNS rebinding.

          It reads the index and never writes it. A page load does not walk the tree: that is what --refresh
          adds, bounded by --index-budget, so the safe behaviour is the one that happens. The prefix is
          127.0.0.1 and there is no --host — the page carries this repository's path and counts, and nothing
          here is published to a network. Opening the URL is your action: VS Code's Simple Browser and a
          Visual Studio browser window both render it. Ctrl+C ends the panel at 0; a port nothing can bind
          ends it at 2, with the port named and --port as the fix.

        Ide
          sharpcoreagent ide agent [options]          the profile that puts this server's tools in an editor's picker
          sharpcoreagent ide tasks [options]          the one task that rebuilds the index, for .vscode/tasks.json
          --write                  ide         off         write the file instead of printing it

          Two files, and neither is per-client: an agent profile is read by whatever editor the developer
          uses, and a task list belongs to this workspace. 'ide agent' writes
          .github/agents/sharpcoreagent.agent.md — YAML frontmatter with a description and a tools list, then
          a prompt. The tools are the six 'mcp serve' publishes, each named sharpcoreagent/<tool>, so the
          profile and the server cannot list different tools. No name: the file's name is the name the picker
          shows. No model and no target: the model is the developer's choice, and unset target means both
          environments.

          It never overwrites somebody else's work. The last line of a profile this command wrote is a marker;
          a file without it is refused at 7 with the file untouched, so a hand-written agent beside this one is
          safe and deleting the marker is how you claim the file.

          'ide tasks' writes .vscode/tasks.json with one task, 'sharpcoreagent: index', whose command is this
          binary's absolute path — the same answer 'mcp config' uses. --write merges by label, so the second run
          replaces the task rather than adding a twin, and every other task and key in the file survives. A file
          that is not JSON, or whose 'tasks' is not an array, exits 7 with the file untouched; a file with //
          comments is refused rather than rewritten, and the task can be added by hand.

          Neither action opens the index, reads a credential, starts a process or touches the network, so both
          work before the first run of anything else. --write says which of the three happened — created,
          updated, unchanged — in one line on stderr, and writes nothing to stdout.

        Chat
          /help                    every chat command, including /cost, /roles and /review

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
          0  the turn completed, or mcp serve reached end of input, or the panel stopped, or an ide file was written
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
          sharpcoreagent chat --planner grok:grok-4.7 --executor deepseek:deepseek-flash
          sharpcoreagent models --provider demo
          sharpcoreagent index --status --json
          sharpcoreagent index --rebuild --workspace .
          sharpcoreagent run "find the TODO comments" --provider openai-compatible --base-url http://localhost:1234/v1
          sharpcoreagent chat --mcp "npx -y @modelcontextprotocol/server-filesystem ."
          sharpcoreagent mcp config --client vscode > .vscode/mcp.json
          sharpcoreagent mcp config --client visualstudio --write
          sharpcoreagent ui --serve
          sharpcoreagent ui --serve --port 8080 --refresh
          sharpcoreagent ui --serve --json
          sharpcoreagent ide agent --write
          sharpcoreagent ide tasks --write
        """;

    /// <summary>Gets the commands the interactive loop understands.</summary>
    public static string ChatCommands { get; } = """
        Commands
          /model                   show the active model
          /model <id>              switch model for this role, keeping the provider
          /model <provider>:<id>   switch provider and model
          /roles                   the model each role runs on: plan, act and review
          /review [focus]          one read-only turn on the reviewer model, in this transcript
          /approval [mode]         readonly, ask or allow; bare reports the mode and what it stops asking about,
                                   allow lasts until exit; /approval forget revokes the yeses given to single tools
          /mode <mode>             plan or act; plan refuses every write and answers with a plan, and the
                                   role for that mode supplies the model when one is named
          /compact [n]             drop old messages, keeping the system prompts and the last n (default 8)
          /budget [n|+n|unlimited] the session token budget; bare reports what is spent
          /continue                carry on from the last request; an empty line does the same after a pause
          /usage                   tokens reported per model, and the total
          /cost                    what the reported tokens cost, from a dated table (peak rate where tiered)
          /files                   files and commands from the last turn
          /undo                    restore the file changes the agent made in the last turn; repeat for earlier turns
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
