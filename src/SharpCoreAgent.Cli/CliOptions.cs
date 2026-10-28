using System.Globalization;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>What the process was asked to do.</summary>
internal enum CliCommand
{
    /// <summary>Print help.</summary>
    Help,

    /// <summary>Print the version.</summary>
    Version,

    /// <summary>Run one turn and exit.</summary>
    Run,

    /// <summary>Run an interactive loop.</summary>
    Chat,

    /// <summary>List the catalogue.</summary>
    Models,

    /// <summary>Build, inspect or repair the code index.</summary>
    Index,

    /// <summary>Read or write the settings file.</summary>
    Config,

    /// <summary>Ask for the preferences one question at a time and write them down.</summary>
    Setup,
}

/// <summary>What <c>config</c> was asked to do.</summary>
internal enum ConfigAction
{
    /// <summary>Print every setting, its value and where the value came from.</summary>
    Show,

    /// <summary>Print one value.</summary>
    Get,

    /// <summary>Write one value.</summary>
    Set,

    /// <summary>Remove one value.</summary>
    Unset,

    /// <summary>Print the file in use.</summary>
    Path,
}

/// <summary>
/// The parsed command line, in the shape <c>contracts/cli.md</c> describes.
/// </summary>
/// <remarks>
/// Parsing is pure: no file is touched, no container is built and no credential is read, so a test can
/// assert on every rule in the contract without a workspace or a provider. Values that can fail later
/// (a directory that does not exist, a budget out of range) are deliberately left to the command that
/// needs them, where the error can name the thing the developer actually asked for.
/// </remarks>
internal sealed record CliOptions
{
    /// <summary>Gets the command.</summary>
    public CliCommand Command { get; init; } = CliCommand.Help;

    /// <summary>
    /// Gets a value indicating whether the command was typed rather than defaulted.
    /// </summary>
    /// <remarks>
    /// A bare <c>sharpcoreagent</c> starts a session for the same reason <c>chat</c> does, but only a
    /// developer who typed nothing needs to be told what the session is: the banner explains a
    /// default, and would be noise in front of a command that already said what it wanted.
    /// </remarks>
    public bool CommandExplicit { get; init; } = true;

    /// <summary>Gets which verb <c>config</c> runs. Default <see cref="ConfigAction.Show"/>.</summary>
    public ConfigAction ConfigAction { get; init; } = ConfigAction.Show;

    /// <summary>Gets the setting <c>config get</c>, <c>set</c> or <c>unset</c> names.</summary>
    public string? ConfigName { get; init; }

    /// <summary>Gets the value <c>config set</c> writes.</summary>
    public string? ConfigValue { get; init; }

    /// <summary>
    /// Gets the settings that were named on this command line, by canonical name.
    /// </summary>
    /// <remarks>
    /// Kept so <c>config show</c> can say which layer a value came from without a second parse: a name
    /// in this set is here because a flag said so, and a flag beats both the environment and a file.
    /// </remarks>
    public IReadOnlySet<string> CommandLineSettings { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets the prompt for <see cref="CliCommand.Run"/>; empty for other commands.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Gets the provider key, matched case-insensitively and normalised.</summary>
    public string Provider { get; init; } = ModelCatalog.DefaultProvider;

    /// <summary>
    /// Gets a value indicating whether the provider was named by the developer rather than defaulted.
    /// </summary>
    /// <remarks>
    /// <c>models</c> needs the difference: naming no provider lists every provider, while
    /// <c>--provider grok</c> narrows the list to one, and both are expressed with the same value.
    /// </remarks>
    public bool ProviderExplicit { get; init; }

    /// <summary>Gets the model identifier, or null for the provider's default.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the workspace root the tools are confined to.</summary>
    public string Workspace { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets the read-only roots the read tools may answer from, in the order they were named.
    /// </summary>
    /// <remarks>
    /// Filled by repeated <c>--add-root</c> and then by <c>SHARPCOREAGENT_ADD_ROOTS</c>. Exactly one setting
    /// feeds it and the values are never persisted: this is authority for one invocation, not a preference, and
    /// <see cref="SettableNames"/> deliberately does not include it.
    /// </remarks>
    public IReadOnlyList<string> AdditionalRoots { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether this invocation may read the web.
    /// </summary>
    /// <remarks>
    /// <c>--web</c>, and nothing else: not a settings key, not an environment variable. The network is a decision about
    /// one task, and a default that outlived the task would be granted by whoever last ran <c>config set</c>.
    /// </remarks>
    public bool Web { get; init; }

    /// <summary>
    /// Gets the hosts this invocation may read even though their addresses are refused, in the order they were named.
    /// </summary>
    /// <remarks>
    /// Filled by repeated <c>--web-allow</c>. It is authority for one invocation, like <see cref="AdditionalRoots"/>, and
    /// <see cref="SettableNames"/> deliberately does not include it.
    /// </remarks>
    public IReadOnlyList<string> WebAllowedHosts { get; init; } = [];

    /// <summary>
    /// Gets the approval mode. Default <see cref="ApprovalMode.Ask"/>.
    /// </summary>
    /// <remarks>
    /// <c>-y</c> and <c>--yes</c> are this property spelled the short way: they set
    /// <see cref="ApprovalMode.AllowAll"/> in the position they appear, so a later <c>--approval readonly</c>
    /// still wins.
    /// </remarks>
    public ApprovalMode Approval { get; init; } = ApprovalMode.Ask;

    /// <summary>Gets the iteration ceiling. Default 400.</summary>
    /// <remarks>
    /// One iteration is one model call, so a turn that reads, edits, builds and retests spends tens of them. The
    /// former default of 24 ended ordinary work with "the iteration budget of 24 model calls ran out"; the ceiling
    /// is still there to stop a model that loops forever.
    /// </remarks>
    public int MaxIterations { get; init; } = 400;

    /// <summary>Gets the per-request output ceiling.</summary>
    public int MaxOutputTokens { get; init; } = 8_000;

    /// <summary>Gets the session token budget. Default 10 000 000.</summary>
    /// <remarks>
    /// A session, not a turn: the ceiling a long conversation reaches at all. It is set high because a session
    /// that stops mid-task for a budget the developer never chose reads as a crash, and because the CLI offers
    /// more room rather than ending the conversation when one is reached (<c>/continue</c>, <c>/budget</c>).
    /// </remarks>
    public long MaxTotalTokens { get; init; } = 10_000_000;

    /// <summary>Gets the sampling temperature.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Gets the endpoint for the <c>openai-compatible</c> provider.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets the path of a credential file, or <see langword="null"/> when the environment is the only
    /// source.
    /// </summary>
    /// <remarks>
    /// Supplied by <c>--credential-file</c> or <c>SHARPCOREAGENT_CREDENTIAL_FILE</c>. Parsing stores the
    /// path and reads nothing: whether the file exists is a question for the command that needs a key,
    /// where the failure can name the file the developer asked for.
    /// </remarks>
    public string? CredentialFile { get; init; }

    /// <summary>
    /// Gets the model a turn that failed with 401 or 5xx is replayed at, or null for no fallback.
    /// </summary>
    /// <remarks>
    /// Parsed here rather than in the command because an unknown provider is a command-line mistake:
    /// the developer reads the list of providers this build knows while typing, not three seconds into
    /// a turn.
    /// </remarks>
    public ModelDescriptor? Fallback { get; init; }

    /// <summary>Gets the transcript path, or null for the timestamped default.</summary>
    public string? SessionPath { get; init; }

    /// <summary>Gets a value indicating whether a transcript should be written.</summary>
    public bool SaveSession { get; init; } = true;

    /// <summary>Gets a value indicating whether events are emitted as JSON lines.</summary>
    public bool Json { get; init; }

    /// <summary>Gets how the terminal decides whether it may use colour. Default <see cref="ColorChoice.Auto"/>.</summary>
    public ColorChoice Color { get; init; } = ColorChoice.Auto;

    /// <summary>Gets whether a turn may change the workspace. Default <see cref="AgentMode.Act"/>.</summary>
    public AgentMode Mode { get; init; } = AgentMode.Act;

    /// <summary>Gets a value indicating whether thinking and tool summaries are suppressed.</summary>
    public bool Quiet { get; init; }

    /// <summary>Gets a value indicating whether diagnostics are shown.</summary>
    public bool Verbose { get; init; }

    /// <summary>Gets a value indicating whether the index may be read, refreshed or built. <c>--no-index</c> clears it.</summary>
    public bool IndexEnabled { get; init; } = true;

    /// <summary>Gets the wall-clock ceiling for the pre-turn refresh, in milliseconds. Default 3000; 0 disables it.</summary>
    public int IndexBudgetMs { get; init; } = 3000;

    /// <summary>Gets the embedder the index uses. Default <see cref="EmbedderKind.Local"/>.</summary>
    public EmbedderKind Embedder { get; init; } = EmbedderKind.Local;

    /// <summary>Gets the detail tier the index keeps. Default <see cref="IndexTier.Lean"/>.</summary>
    public IndexTier Tier { get; init; } = IndexTier.Lean;

    /// <summary>Gets a value indicating whether <c>index</c> should print coverage and freshness.</summary>
    public bool IndexStatus { get; init; }

    /// <summary>Gets a value indicating whether <c>index</c> should throw the index away and build it again.</summary>
    public bool IndexRebuild { get; init; }

    /// <summary>Gets a value indicating whether <c>index</c> should report drift without writing.</summary>
    public bool IndexVerify { get; init; }

    /// <summary>Gets a value indicating whether <c>index</c> should drop rows for vanished files.</summary>
    public bool IndexPrune { get; init; }

    /// <summary>Projects the command line onto the container's options.</summary>
    /// <returns>The options <c>AddSharpCoreAgent</c> consumes.</returns>
    public SharpCoreAgentOptions ToSharpCoreAgentOptions() => new()
    {
        Provider = Provider,
        ModelId = Model,
        WorkspaceRoot = Workspace,
        AdditionalRoots = AdditionalRoots,
        WebAccessEnabled = Web,
        WebAllowedHosts = WebAllowedHosts,
        Approval = Approval,
        MaxIterations = MaxIterations,
        MaxOutputTokensPerTurn = MaxOutputTokens,
        MaxTotalTokens = MaxTotalTokens,
        Temperature = Temperature,
        BaseUrl = BaseUrl,
        CredentialFilePath = CredentialFile,
        Fallback = Fallback,
        IndexEnabled = IndexEnabled,
        IndexBudget = TimeSpan.FromMilliseconds(IndexBudgetMs),
    };

    /// <summary>
    /// Builds the default transcript path: <c>&lt;workspace&gt;/.sharpcoreagent/sessions/&lt;timestamp&gt;.json</c>.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root.</param>
    /// <param name="now">The moment the session started.</param>
    /// <returns>An absolute path that does not depend on the process's culture.</returns>
    public static string DefaultSessionPath(string workspaceRoot, DateTimeOffset now) => Path.Combine(
        workspaceRoot,
        ".sharpcoreagent",
        "sessions",
        string.Create(
            CultureInfo.InvariantCulture,
            $"{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.json"));

    /// <summary>
    /// Parses a command line.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <param name="readEnvironment">The environment reader; defaults to the process environment.</param>
    /// <param name="isInteractive">The terminal probe; defaults to the process's own console.</param>
    /// <param name="loadSettings">
    /// Loads the settings document, or <see langword="null"/> for a process with no settings file. It is
    /// a loader rather than an instance so the file is only read by the commands that read settings: a
    /// broken file must not stop <c>config set</c> from repairing it.
    /// </param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="CliUsageException">An argument is unknown, incomplete or contradictory.</exception>
    /// <exception cref="SharpCoreAgent.Domain.Errors.ConfigurationFileException">The settings file cannot be applied.</exception>
    public static CliOptions Parse(
        string[] args,
        Func<string, string?>? readEnvironment = null,
        Func<bool>? isInteractive = null,
        Func<CliSettings>? loadSettings = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var environment = readEnvironment ?? Environment.GetEnvironmentVariable;
        var options = FromEnvironment(environment);

        // A shell alias may leave the product's former name in front of the command, so `ariadne index` must behave
        // exactly like `sharpcoreagent index`. Only a leading token is dropped, which keeps a prompt that happens to
        // start with the word intact.
        if (args.Length > 0 && Is(args[0], "ariadne"))
        {
            args = args[1..];
        }

        var interactive = isInteractive ?? Terminal.Interactive;
        CliOptions parsed;
        if (args.Length == 0)
        {
            parsed = options with
            {
                Command = interactive() ? CliCommand.Chat : CliCommand.Help,
                CommandExplicit = false,
            };
        }
        else
        {
            parsed = ScanArguments(args, options, interactive) with
            {
                CommandLineSettings = CommandLineNames(args),
            };
        }

        // A root is authority for a session rather than a preference for the build, so the variable is read
        // only where a session exists: `models` and `config` never open a workspace, and `index` describes
        // exactly one tree. Its roots come after the ones typed here, in the order given, so the command line
        // wins the order without either list being re-sorted.
        if (parsed.Command is CliCommand.Run or CliCommand.Chat)
        {
            parsed = parsed with
            {
                AdditionalRoots = [.. parsed.AdditionalRoots, .. SplitRoots(environment("SHARPCOREAGENT_ADD_ROOTS"))],
            };

            // A host named without the switch that would use it is refused, not ignored: the developer believes they have
            // granted a host, and finding out at the refusal instead is how a usable tool turns into a mysterious one.
            if (parsed.WebAllowedHosts.Count > 0 && !parsed.Web)
            {
                throw new CliUsageException(
                    "--web-allow needs --web.",
                    "For example: sharpcoreagent run \"read the changelog\" --web --web-allow localhost");
            }
        }

        return ApplySettings(parsed, loadSettings, environment);
    }

    /// <summary>
    /// Splits the <c>SHARPCOREAGENT_ADD_ROOTS</c> value into its entries.
    /// </summary>
    /// <param name="value">The variable's value, or <see langword="null"/> when it is unset.</param>
    /// <returns>The entries, in order, with blank ones dropped.</returns>
    /// <remarks>
    /// Split on <see cref="Path.PathSeparator"/> — <c>;</c> on Windows, <c>:</c> elsewhere — because a Windows
    /// absolute path already contains a colon, so one separator cannot serve both platforms.
    /// </remarks>
    public static IReadOnlyList<string> SplitRoots(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : [.. value.Split(Path.PathSeparator).Select(entry => entry.Trim()).Where(entry => entry.Length > 0)];

    /// <summary>
    /// Gets the names <c>config</c> accepts, in the order <c>config show</c> prints them.
    /// </summary>
    /// <remarks>
    /// The list is the flags that describe a developer's defaults rather than one invocation. Everything
    /// left out (<c>--quiet</c>, <c>--json</c>, <c>--color</c>, <c>--session</c>, the index flags) is a
    /// property of the run it appears on; a persisted <c>--json</c> would be a bug waiting for the
    /// developer who pipes one command.
    /// </remarks>
    public static IReadOnlyList<string> SettableNames { get; } =
    [
        "provider",
        "model",
        "base-url",
        "approval",
        "mode",
        "temperature",
        "max-iterations",
        "max-output-tokens",
        "max-total-tokens",
        "credential-file",
    ];

    /// <summary>
    /// Applies one setting to a set of options, through the option parser.
    /// </summary>
    /// <param name="options">The options to change.</param>
    /// <param name="name">A name from <see cref="SettableNames"/>.</param>
    /// <param name="value">The text the developer or the settings file supplied.</param>
    /// <returns>The options with that one setting replaced.</returns>
    /// <exception cref="CliUsageException">The value is one the equivalent flag would reject.</exception>
    /// <remarks>
    /// The value is validated by re-entering the scanner with the equivalent flag, so a value that
    /// arrives from a file and one that arrives on the command line cannot drift apart: there is one
    /// parser and therefore one set of rules, one message and one range.
    /// </remarks>
    public static CliOptions WithSetting(CliOptions options, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var flag = FlagFor(name)
            ?? throw new CliUsageException(
                $"'{name}' is not a setting.",
                $"Settings are {string.Join(", ", SettableNames)}.");

        return Scan([flag, value], options).Options;
    }

    /// <summary>
    /// Returns the canonical name of a setting, or throws the reason it is not one.
    /// </summary>
    /// <param name="name">The name as the developer typed it, in any case, with either separator.</param>
    /// <returns>The canonical kebab-case name.</returns>
    /// <exception cref="CliUsageException">The name is unknown, or is a credential.</exception>
    /// <remarks>
    /// A credential is refused with its own message rather than lumped in with the unknown names: the
    /// developer is not confused about the spelling, they are trying to do something the file must not
    /// do, and the answer is a different command.
    /// </remarks>
    public static string EnsureSettable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (CanonicalName(name) is { } canonical)
        {
            return canonical;
        }

        if (IsCredentialName(NormalizeName(name)))
        {
            throw new CliUsageException(
                $"'{name}' is a credential, and the settings file is not a home for one.",
                "Put it in a NAME=value file and pass --credential-file, or export it; the settings file is for preferences.");
        }

        throw new CliUsageException(
            $"'{name}' is not a setting.",
            $"Settings are {string.Join(", ", SettableNames)}.");
    }

    /// <summary>
    /// Folds a name onto the setting it means.
    /// </summary>
    /// <param name="name">The name as written, in any case, with either separator.</param>
    /// <returns>The canonical kebab-case name, or <see langword="null"/> when it names no setting.</returns>
    public static string? CanonicalName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : SettableNames.FirstOrDefault(setting =>
                string.Equals(NormalizeName(setting), NormalizeName(name), StringComparison.Ordinal));

    /// <summary>
    /// Compares two names without caring about case or separator.
    /// </summary>
    /// <param name="left">One name.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when both fold to the same text.</returns>
    public static bool IsSameName(string left, string right) =>
        string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.Ordinal);

    /// <summary>
    /// Reads the defaults the environment supplies.
    /// </summary>
    /// <param name="environment">The environment reader.</param>
    /// <returns>Options with the provider, model and workspace the environment names.</returns>
    /// <exception cref="CliUsageException">The environment names a provider this build does not serve.</exception>
    private static CliOptions FromEnvironment(Func<string, string?> environment) => new()
    {
        Provider = NormalizeProvider(environment("SHARPCOREAGENT_PROVIDER")),
        ProviderExplicit = Trim(environment("SHARPCOREAGENT_PROVIDER")) is not null,
        Model = Trim(environment("SHARPCOREAGENT_MODEL")),
        Workspace = Trim(environment("SHARPCOREAGENT_WORKSPACE")) ?? Directory.GetCurrentDirectory(),
        CredentialFile = Trim(environment("SHARPCOREAGENT_CREDENTIAL_FILE")),
    };

    /// <summary>
    /// Walks the arguments, applying options and collecting the command and its positionals.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <param name="options">The options to start from.</param>
    /// <param name="interactive">The terminal probe, asked only when no command was named.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="CliUsageException">An argument is unknown or incomplete.</exception>
    private static CliOptions ScanArguments(string[] args, CliOptions options, Func<bool> interactive)
    {
        var (scanned, command, positionals) = Scan(args, options);
        return Finish(scanned, command, positionals, interactive);
    }

    /// <summary>
    /// Walks the arguments, applying options and collecting the command and its positionals.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <param name="options">The options to start from.</param>
    /// <returns>The options the flags produced, the command, and the arguments that were not options.</returns>
    /// <exception cref="CliUsageException">An argument is unknown or incomplete.</exception>
    /// <remarks>
    /// Split from <see cref="Finish"/> because a settings value has to travel through the same flag
    /// handling without deciding a command: <c>--approval ask</c> from a file and from a prompt must be
    /// validated identically, and only the prompt version gets to name a command.
    /// </remarks>
    private static (CliOptions Options, CliCommand? Command, List<string> Positionals) Scan(
        string[] args,
        CliOptions options)
    {
        CliCommand? command = null;
        var positionals = new List<string>();
        var optionsEnded = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (!optionsEnded && TryParseFlag(argument, out var flag))
            {
                return (options with { Command = flag }, flag, positionals);
            }

            if (!optionsEnded && (Is(argument, "-y") || Is(argument, "--yes")))
            {
                // The one-letter answer to every approval question. It is a shorthand rather than a
                // separate concept: `-y` and `--approval allow` produce the same options, so the settings
                // file, `config show` and the transcript cannot disagree about what was requested.
                options = options with { Approval = ApprovalMode.AllowAll };
                continue;
            }

            if (!optionsEnded && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && argument.StartsWith("--", StringComparison.Ordinal))
            {
                options = ApplyOption(options, args, ref index, argument);
                continue;
            }

            if (!optionsEnded && argument.Length > 1 && argument[0] == '-')
            {
                throw new CliUsageException(
                    $"unknown option '{argument}'.",
                    "Run 'sharpcoreagent --help' to see the options.");
            }

            if (command is null)
            {
                command = ParseCommand(argument);
                continue;
            }

            positionals.Add(argument);
        }

        return (options, command, positionals);
    }

    /// <summary>
    /// Turns the collected command and positionals into validated options.
    /// </summary>
    /// <param name="options">The options the flags produced.</param>
    /// <param name="command">The command, or <see langword="null"/> when none was named.</param>
    /// <param name="positionals">The arguments that were not options.</param>
    /// <param name="interactive">The terminal probe, which decides what "no command" means.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="CliUsageException">The command and its arguments contradict each other.</exception>
    private static CliOptions Finish(
        CliOptions options,
        CliCommand? command,
        List<string> positionals,
        Func<bool> interactive)
    {
        switch (command)
        {
            case null:
                // Nothing was typed. With a keyboard attached that is a request for a conversation; from
                // a pipe, a here-document or a CI runner there is nobody to prompt, so it stays an
                // explanation. A REPL that cannot be typed into is not a REPL, and `sharpcoreagent` in a
                // script must never block.
                return options with
                {
                    Command = interactive() ? CliCommand.Chat : CliCommand.Help,
                    CommandExplicit = false,
                };

            case CliCommand.Help:
            case CliCommand.Version:
                return options with { Command = command.Value };

            case CliCommand.Run:
                RejectIndexFlags(options);
                var prompt = string.Join(' ', positionals).Trim();
                if (prompt.Length == 0)
                {
                    throw new CliUsageException(
                        "run needs a prompt.",
                        "For example: sharpcoreagent run \"list the files in src\"");
                }

                return options with { Command = CliCommand.Run, Prompt = prompt };

            case CliCommand.Chat:
                RejectIndexFlags(options);
                if (positionals.Count > 0)
                {
                    throw new CliUsageException(
                        $"chat does not take a prompt, but got '{positionals[0]}'.",
                        "Use 'sharpcoreagent run \"...\"' for a one-shot turn.");
                }

                if (options.Json)
                {
                    throw new CliUsageException("--json is only valid with run.");
                }

                return options with { Command = CliCommand.Chat };

            case CliCommand.Index:
                RejectAttachmentRoots(options);
                RejectWebFlags(options);
                if (positionals.Count > 0)
                {
                    throw new CliUsageException(
                        $"index does not take '{positionals[0]}'.",
                        "Use --status, --rebuild, --verify or --prune.");
                }

                if (!options.IndexEnabled)
                {
                    throw new CliUsageException("the index command always uses the index.");
                }

                if (options.IndexRebuild && (options.IndexStatus || options.IndexVerify || options.IndexPrune))
                {
                    throw new CliUsageException("--rebuild cannot be combined with --status, --verify or --prune.");
                }

                return options with { Command = CliCommand.Index };

            case CliCommand.Config:
                RejectIndexFlags(options);
                RejectAttachmentRoots(options);
                RejectWebFlags(options);
                if (options.Json)
                {
                    throw new CliUsageException(
                        "--json is only valid with run and index.",
                        "Use 'sharpcoreagent config get <name>' for one value a script can read.");
                }

                return FinishConfig(options, positionals);

            case CliCommand.Setup:
                RejectIndexFlags(options);
                RejectAttachmentRoots(options);
                RejectWebFlags(options);
                if (options.Json)
                {
                    throw new CliUsageException(
                        "--json is only valid with run and index.",
                        "Use 'sharpcoreagent config get <name>' for one value a script can read.");
                }

                if (positionals.Count > 0)
                {
                    throw new CliUsageException(
                        $"setup does not take '{positionals[0]}'.",
                        "Run 'sharpcoreagent setup' and answer the questions.");
                }

                return options with { Command = CliCommand.Setup };

            default:
                RejectIndexFlags(options);
                RejectAttachmentRoots(options);
                RejectWebFlags(options);
                if (positionals.Count > 0)
                {
                    throw new CliUsageException($"models does not take '{positionals[0]}'.");
                }

                return options with { Command = CliCommand.Models };
        }
    }

    /// <summary>
    /// Reads the verb and the arguments of <c>config</c>.
    /// </summary>
    /// <param name="options">The options the flags produced.</param>
    /// <param name="positionals">The arguments after the command.</param>
    /// <returns>The options with the action and its operands.</returns>
    /// <exception cref="CliUsageException">The verb is unknown, or its operands do not fit it.</exception>
    private static CliOptions FinishConfig(CliOptions options, List<string> positionals)
    {
        if (positionals.Count == 0)
        {
            return options with { Command = CliCommand.Config, ConfigAction = ConfigAction.Show };
        }

        var rest = positionals.Skip(1).ToList();
        switch (positionals[0].ToLowerInvariant())
        {
            case "show":
                RejectConfigArguments("show", rest);
                return options with { Command = CliCommand.Config, ConfigAction = ConfigAction.Show };

            case "path":
                RejectConfigArguments("path", rest);
                return options with { Command = CliCommand.Config, ConfigAction = ConfigAction.Path };

            case "get":
                return options with
                {
                    Command = CliCommand.Config,
                    ConfigAction = ConfigAction.Get,
                    ConfigName = EnsureSettable(ConfigOperand("get", rest)),
                };

            case "unset":
                // Any name is accepted: this is the verb that removes a key a newer build wrote, or a
                // typo, so it must not be the verb that refuses one.
                return options with
                {
                    Command = CliCommand.Config,
                    ConfigAction = ConfigAction.Unset,
                    ConfigName = ConfigOperand("unset", rest),
                };

            case "set":
                if (rest.Count < 2)
                {
                    throw new CliUsageException(
                        "config set needs a setting and a value.",
                        "For example: sharpcoreagent config set approval readonly");
                }

                return options with
                {
                    Command = CliCommand.Config,
                    ConfigAction = ConfigAction.Set,
                    ConfigName = EnsureSettable(rest[0]),
                    ConfigValue = string.Join(' ', rest.Skip(1)).Trim(),
                };

            default:
                throw new CliUsageException(
                    $"'{positionals[0]}' is not a config action.",
                    "Actions are show, get, set, unset and path.");
        }
    }

    /// <summary>
    /// Refuses an operand on a verb that takes none.
    /// </summary>
    /// <param name="verb">The verb, for the message.</param>
    /// <param name="rest">The operands it was given.</param>
    /// <exception cref="CliUsageException">There was at least one.</exception>
    private static void RejectConfigArguments(string verb, List<string> rest)
    {
        if (rest.Count > 0)
        {
            throw new CliUsageException($"config {verb} does not take '{rest[0]}'.");
        }
    }

    /// <summary>
    /// Takes the one setting name a verb needs.
    /// </summary>
    /// <param name="verb">The verb, for the messages.</param>
    /// <param name="rest">The operands it was given.</param>
    /// <returns>The name, unchanged.</returns>
    /// <exception cref="CliUsageException">There was not exactly one operand.</exception>
    private static string ConfigOperand(string verb, List<string> rest) => rest.Count == 1
        ? rest[0]
        : throw new CliUsageException(
            $"config {verb} needs one setting name.",
            $"For example: sharpcoreagent config {verb} provider");

    /// <summary>
    /// Applies the settings file underneath the environment and the command line.
    /// </summary>
    /// <param name="options">What the environment and the arguments already decided.</param>
    /// <param name="loadSettings">The loader, or <see langword="null"/> for no file.</param>
    /// <param name="environment">The environment reader, which outranks the file.</param>
    /// <returns>The options a command should run with.</returns>
    /// <exception cref="ConfigurationFileException">A value in the file is not one this build accepts.</exception>
    private static CliOptions ApplySettings(
        CliOptions options,
        Func<CliSettings>? loadSettings,
        Func<string, string?> environment)
    {
        if (loadSettings is null)
        {
            return options;
        }

        // Help and the version are questions about the build, not about this developer's setup: they must
        // answer even when the file that says which model to use cannot be read.
        if (options.Command is CliCommand.Help or CliCommand.Version)
        {
            return options;
        }

        // The verbs that write or locate the file read it as a document only. A value this build cannot
        // apply must never be the reason its own repair command refuses to run.
        if (options.Command == CliCommand.Config
            && options.ConfigAction is ConfigAction.Set or ConfigAction.Unset or ConfigAction.Path)
        {
            return options;
        }

        var settings = loadSettings();
        if (settings.Values.Count == 0)
        {
            return options;
        }

        var merged = options;
        foreach (var (rawName, value) in settings.Values)
        {
            var name = SettableNameIn(rawName, settings.Path);
            if (options.CommandLineSettings.Contains(name)
                || (EnvironmentVariableFor(name) is { } variable && !string.IsNullOrWhiteSpace(environment(variable))))
            {
                // A flag the developer typed, or an export in the shell, is a decision about this run: the
                // file is a default, and a default never beats a decision.
                continue;
            }

            try
            {
                merged = WithSetting(merged, name, value);
            }
            catch (CliUsageException ex)
            {
                throw new ConfigurationFileException(
                    $"the settings file '{settings.Path}' sets '{name}' to a value this build refuses: {ex.Message}",
                    ex);
            }
        }

        EnsureUsable(merged, settings.Path);
        return merged;
    }

    /// <summary>
    /// Canonicalises a name from the file, or reports the file's mistake.
    /// </summary>
    /// <param name="rawName">The key as written.</param>
    /// <param name="path">The file it came from, for the message.</param>
    /// <returns>The canonical name.</returns>
    /// <exception cref="ConfigurationFileException">The key names no setting this build has.</exception>
    private static string SettableNameIn(string rawName, string path)
    {
        try
        {
            return EnsureSettable(rawName);
        }
        catch (CliUsageException ex)
        {
            throw new ConfigurationFileException(
                $"the settings file '{path}' cannot be applied: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Checks that the file's budgets and temperature describe a session this build can run.
    /// </summary>
    /// <param name="options">The merged options.</param>
    /// <param name="path">The file, for the message.</param>
    /// <exception cref="ConfigurationFileException">A limit is outside its range.</exception>
    /// <remarks>
    /// The ranges live in <c>AgentOptions.Validate</c>, not here: a file that says
    /// <c>"temperature": "5"</c> would otherwise be accepted at parse time and fail at the first turn,
    /// where the message no longer knows which file to blame.
    /// </remarks>
    private static void EnsureUsable(CliOptions options, string path)
    {
        try
        {
            options.ToSharpCoreAgentOptions().ToAgentOptions();
        }
        catch (ArgumentException ex)
        {
            throw new ConfigurationFileException(
                $"the settings file '{path}' holds a value this build refuses: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Names the settings that were given as flags.
    /// </summary>
    /// <param name="args">The arguments, after the product name was dropped.</param>
    /// <returns>The canonical names, in no particular order.</returns>
    private static HashSet<string> CommandLineNames(string[] args)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in args)
        {
            if (SettingFor(argument) is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string? SettingFor(string flag) => flag switch
    {
        "--provider" => "provider",
        "--model" => "model",
        "--base-url" => "base-url",
        "--approval" => "approval",

        // The shorthand names the same setting, so `config show` says "command line" for approval when
        // the developer typed -y instead of leaving the row looking like a default.
        "-y" or "--yes" => "approval",
        "--mode" => "mode",
        "--temperature" => "temperature",
        "--max-iterations" => "max-iterations",
        "--max-output-tokens" => "max-output-tokens",
        "--max-total-tokens" => "max-total-tokens",
        "--credential-file" => "credential-file",
        _ => null,
    };

    private static string? FlagFor(string name) => name switch
    {
        "provider" => "--provider",
        "model" => "--model",
        "base-url" => "--base-url",
        "approval" => "--approval",
        "mode" => "--mode",
        "temperature" => "--temperature",
        "max-iterations" => "--max-iterations",
        "max-output-tokens" => "--max-output-tokens",
        "max-total-tokens" => "--max-total-tokens",
        "credential-file" => "--credential-file",
        _ => null,
    };

    /// <summary>
    /// Folds a setting name to the form two spellings can be compared in.
    /// </summary>
    /// <param name="name">The name as written.</param>
    /// <returns>Its letters and digits, lower-cased.</returns>
    /// <remarks>
    /// <c>maxIterations</c>, <c>max-iterations</c> and <c>MAX_ITERATIONS</c> are one setting; refusing
    /// one of them would be a spelling lesson rather than a configuration.
    /// </remarks>
    private static string NormalizeName(string name) =>
        new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// Recognises a name that is a secret rather than a setting.
    /// </summary>
    /// <param name="normalized">A name folded by <see cref="NormalizeName"/>.</param>
    /// <returns><see langword="true"/> when the name is a credential.</returns>
    private static bool IsCredentialName(string normalized) =>
        normalized.EndsWith("key", StringComparison.Ordinal)
        || normalized.EndsWith("secret", StringComparison.Ordinal)
        || normalized is "token" or "authtoken" or "password"
        || ModelCatalog.Providers.Any(provider => ModelCatalog
            .CredentialNames(provider)
            .Any(name => string.Equals(NormalizeName(name), normalized, StringComparison.Ordinal)));

    /// <summary>
    /// Names the environment variable that supplies a setting, when one exists.
    /// </summary>
    /// <param name="name">The canonical setting name.</param>
    /// <returns>The variable, or <see langword="null"/> when the environment cannot supply it.</returns>
    public static string? EnvironmentVariableFor(string name) => name switch
    {
        "provider" => "SHARPCOREAGENT_PROVIDER",
        "model" => "SHARPCOREAGENT_MODEL",
        "credential-file" => "SHARPCOREAGENT_CREDENTIAL_FILE",
        _ => null,
    };

    /// <summary>
    /// Rejects the flags that only make sense on <c>index</c> when they appear on a turn command.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    /// <exception cref="CliUsageException">One of the index action flags was given.</exception>
    /// <remarks>
    /// Silently ignoring <c>--rebuild</c> on <c>run</c> would be the worst outcome: the developer would believe the
    /// index was rebuilt and read stale answers for the rest of the session.
    /// </remarks>
    private static void RejectIndexFlags(CliOptions options)
    {
        if (options.IndexStatus || options.IndexRebuild || options.IndexVerify || options.IndexPrune)
        {
            throw new CliUsageException(
                "--status, --rebuild, --verify and --prune belong to the index command.",
                "For example: sharpcoreagent index --verify");
        }
    }

    /// <summary>
    /// Rejects <c>--add-root</c> on the commands that have no workspace to attach a root to.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    /// <exception cref="CliUsageException">A root was named for a command that cannot use one.</exception>
    /// <remarks>
    /// Accepting the flag and ignoring it would be worse than refusing it: the developer would believe the
    /// session could read the other repository and only find out when a read was refused.
    /// </remarks>
    private static void RejectAttachmentRoots(CliOptions options)
    {
        if (options.AdditionalRoots.Count > 0)
        {
            throw new CliUsageException(
                "--add-root applies to run and chat.",
                "For example: sharpcoreagent chat --add-root ..\\Shared.Contracts");
        }
    }

    /// <summary>
    /// Rejects <c>--web</c> and <c>--web-allow</c> on the commands that never make a request.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    /// <exception cref="CliUsageException">The network was asked for on a command that cannot use it.</exception>
    /// <remarks>
    /// The same reasoning as <see cref="RejectAttachmentRoots"/>, with a sharper edge: a developer who typed <c>--web</c>
    /// and saw nothing happen would conclude the fetch was refused by the safety rules rather than never offered.
    /// </remarks>
    private static void RejectWebFlags(CliOptions options)
    {
        if (options.Web || options.WebAllowedHosts.Count > 0)
        {
            throw new CliUsageException(
                "--web and --web-allow apply to run and chat.",
                "For example: sharpcoreagent run \"read the release notes\" --web");
        }
    }

    /// <summary>
    /// Parses an approval mode name.
    /// </summary>
    /// <param name="value">The text to parse, case-insensitively.</param>
    /// <param name="mode">The mode, when the text names one.</param>
    /// <returns><see langword="true"/> when the text was understood.</returns>
    public static bool TryParseApproval(string? value, out ApprovalMode mode)
    {
        mode = ApprovalMode.Ask;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (Is(text, "readonly") || Is(text, "read-only") || Is(text, "read_only"))
        {
            mode = ApprovalMode.ReadOnly;
            return true;
        }

        if (Is(text, "ask"))
        {
            mode = ApprovalMode.Ask;
            return true;
        }

        if (Is(text, "allow") || Is(text, "allowall") || Is(text, "allow-all"))
        {
            mode = ApprovalMode.AllowAll;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a mode name.
    /// </summary>
    /// <param name="value">The text to parse, case-insensitively.</param>
    /// <param name="mode">The mode, when the text names one.</param>
    /// <returns><see langword="true"/> when the text was understood.</returns>
    public static bool TryParseMode(string? value, out AgentMode mode)
    {
        mode = AgentMode.Act;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (Is(text, "plan") || Is(text, "read-only"))
        {
            mode = AgentMode.Plan;
            return true;
        }

        if (Is(text, "act") || Is(text, "edit"))
        {
            mode = AgentMode.Act;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a colour choice.
    /// </summary>
    /// <param name="value">The text to parse, case-insensitively.</param>
    /// <param name="choice">The choice, when the text names one.</param>
    /// <returns><see langword="true"/> when the text was understood.</returns>
    public static bool TryParseColor(string? value, out ColorChoice choice)
    {
        choice = ColorChoice.Auto;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (Is(text, "auto"))
        {
            return true;
        }

        if (Is(text, "always") || Is(text, "on"))
        {
            choice = ColorChoice.Always;
            return true;
        }

        if (Is(text, "never") || Is(text, "off"))
        {
            choice = ColorChoice.Never;
            return true;
        }

        return false;
    }

    private static AgentMode ParseMode(string value) => TryParseMode(value, out var mode)
        ? mode
        : throw new CliUsageException($"unknown mode '{value}'.", "Use plan or act.");

    private static ColorChoice ParseColor(string value) => TryParseColor(value, out var choice)
        ? choice
        : throw new CliUsageException($"unknown colour mode '{value}'.", "Use auto, always or never.");

    /// <summary>
    /// Parses <c>--fallback</c>, which names a model the same way <c>/model</c> does.
    /// </summary>
    /// <param name="value">The <c>provider:model</c> text.</param>
    /// <returns>The descriptor, taken from the catalogue when the identifier is listed.</returns>
    /// <exception cref="CliUsageException">The text is not a known provider followed by an identifier.</exception>
    /// <remarks>
    /// An unknown provider is refused here rather than after the first 401: a fallback that cannot be
    /// built is worse than no fallback, because the developer has stopped watching for the failure it
    /// was meant to cover.
    /// </remarks>
    private static ModelDescriptor ParseFallback(string value) =>
        ModelCatalog.TryParseQualified(value, out var descriptor)
            ? descriptor!
            : throw new CliUsageException(
                $"'{value}' is not a fallback this build can use.",
                $"Name a provider this build knows ({string.Join(", ", ModelCatalog.Providers)}) and a model, for example grok:grok-4.7.");

    private static CliOptions ApplyOption(CliOptions options, string[] args, ref int index, string name)
    {
        switch (name)
        {
            case "--provider":
                return options with
                {
                    Provider = NormalizeProvider(TakeValue(args, ref index, name)),
                    ProviderExplicit = true,
                };

            case "--model":
                return options with { Model = TakeValue(args, ref index, name) };

            case "--workspace":
                return options with { Workspace = TakeValue(args, ref index, name) };

            case "--add-root":
                return options with
                {
                    AdditionalRoots = [.. options.AdditionalRoots, TakeValue(args, ref index, name)],
                };

            case "--web":
                return options with { Web = true };

            case "--web-allow":
                return options with
                {
                    WebAllowedHosts = [.. options.WebAllowedHosts, TakeValue(args, ref index, name)],
                };

            case "--approval":
                var approval = TakeValue(args, ref index, name);
                return TryParseApproval(approval, out var mode)
                    ? options with { Approval = mode }
                    : throw new CliUsageException(
                        $"unknown approval mode '{approval}'.",
                        "Use readonly, ask or allow.");

            case "--max-iterations":
                return options with { MaxIterations = ParseInt(name, TakeValue(args, ref index, name)) };

            case "--max-output-tokens":
                return options with { MaxOutputTokens = ParseInt(name, TakeValue(args, ref index, name)) };

            case "--max-total-tokens":
                return options with { MaxTotalTokens = ParseLong(name, TakeValue(args, ref index, name)) };

            case "--temperature":
                return options with { Temperature = ParseDouble(name, TakeValue(args, ref index, name)) };

            case "--base-url":
                return options with { BaseUrl = TakeValue(args, ref index, name) };

            case "--credential-file":
                return options with { CredentialFile = TakeValue(args, ref index, name) };

            case "--fallback":
                return options with { Fallback = ParseFallback(TakeValue(args, ref index, name)) };

            case "--session":
                return options with { SessionPath = TakeValue(args, ref index, name), SaveSession = true };

            case "--no-session":
                return options with { SessionPath = null, SaveSession = false };

            case "--json":
                return options with { Json = true };

            case "--color":
                return options with { Color = ParseColor(TakeValue(args, ref index, name)) };

            case "--mode":
                return options with { Mode = ParseMode(TakeValue(args, ref index, name)) };

            case "--quiet":
                return options with { Quiet = true };

            case "--verbose":
                return options with { Verbose = true };

            case "--no-index":
                return options with { IndexEnabled = false };

            case "--index-budget":
                return options with { IndexBudgetMs = ParseBudget(name, TakeValue(args, ref index, name)) };

            case "--embedder":
                return options with { Embedder = ParseEmbedder(TakeValue(args, ref index, name)) };

            case "--tier":
                return options with { Tier = ParseTier(TakeValue(args, ref index, name)) };

            case "--status":
                return options with { IndexStatus = true };

            case "--rebuild":
                return options with { IndexRebuild = true };

            case "--verify":
                return options with { IndexVerify = true };

            case "--prune":
                return options with { IndexPrune = true };

            default:
                throw new CliUsageException(
                    $"unknown option '{name}'.",
                    "Run 'sharpcoreagent --help' to see the options.");
        }
    }

    private static bool TryParseFlag(string argument, out CliCommand command)
    {
        command = CliCommand.Help;
        if (Is(argument, "--help") || Is(argument, "-h") || Is(argument, "-?"))
        {
            command = CliCommand.Help;
            return true;
        }

        if (Is(argument, "--version") || Is(argument, "-V"))
        {
            command = CliCommand.Version;
            return true;
        }

        return false;
    }

    private static CliCommand ParseCommand(string argument)
    {
        if (Is(argument, "run"))
        {
            return CliCommand.Run;
        }

        if (Is(argument, "chat"))
        {
            return CliCommand.Chat;
        }

        if (Is(argument, "models"))
        {
            return CliCommand.Models;
        }

        if (Is(argument, "index"))
        {
            return CliCommand.Index;
        }

        if (Is(argument, "config"))
        {
            return CliCommand.Config;
        }

        if (Is(argument, "setup"))
        {
            return CliCommand.Setup;
        }

        if (Is(argument, "help"))
        {
            return CliCommand.Help;
        }

        if (Is(argument, "version"))
        {
            return CliCommand.Version;
        }

        throw new CliUsageException(
            $"'{argument}' is not a command.",
            "Commands are run, chat, models, index, setup and config.");
    }

    private static string TakeValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new CliUsageException($"option '{name}' needs a value.");
        }

        var value = args[index + 1];
        if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"option '{name}' needs a value.");
        }

        index++;
        return value;
    }

    private static int ParseInt(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new CliUsageException($"option '{name}' needs a whole number, but got '{value}'.");
        }

        return parsed;
    }

    private static long ParseLong(string name, string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new CliUsageException($"option '{name}' needs a whole number, but got '{value}'.");
        }

        return parsed;
    }

    private static double ParseDouble(string name, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new CliUsageException($"option '{name}' needs a number, but got '{value}'.");
        }

        return parsed;
    }

    /// <summary>
    /// Parses the refresh budget, which may be zero but never negative.
    /// </summary>
    /// <param name="name">The option name, used in the error message.</param>
    /// <param name="value">The text to parse.</param>
    /// <returns>The budget in milliseconds.</returns>
    /// <exception cref="CliUsageException">The text is not a whole number, or is negative.</exception>
    private static int ParseBudget(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new CliUsageException(
                $"option '{name}' needs a whole number of milliseconds, zero or greater, but got '{value}'.");
        }

        return parsed;
    }

    /// <summary>
    /// Parses the embedder mode.
    /// </summary>
    /// <param name="value">The text to parse, case-insensitively.</param>
    /// <returns>The embedder.</returns>
    /// <exception cref="CliUsageException">The text names no mode this build knows.</exception>
    private static EmbedderKind ParseEmbedder(string value)
    {
        if (Is(value, "hashed-local") || Is(value, "local"))
        {
            return EmbedderKind.Local;
        }

        if (Is(value, "provider"))
        {
            return EmbedderKind.Provider;
        }

        if (Is(value, "off"))
        {
            return EmbedderKind.Off;
        }

        throw new CliUsageException(
            $"unknown embedder '{value}'.",
            "Use hashed-local, provider or off.");
    }

    /// <summary>
    /// Parses the detail tier.
    /// </summary>
    /// <param name="value">The text to parse, case-insensitively.</param>
    /// <returns>The tier.</returns>
    /// <exception cref="CliUsageException">The text names no tier this build knows.</exception>
    private static IndexTier ParseTier(string value)
    {
        if (Is(value, "lean"))
        {
            return IndexTier.Lean;
        }

        if (Is(value, "rich"))
        {
            return IndexTier.Rich;
        }

        throw new CliUsageException($"unknown tier '{value}'.", "Use lean or rich.");
    }

    private static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return ModelCatalog.DefaultProvider;
        }

        var key = provider.Trim();
        var known = ModelCatalog.Providers.FirstOrDefault(candidate => Is(candidate, key));
        if (known is not null)
        {
            return known;
        }

        throw new CliUsageException(
            $"'{key}' is not a provider this build knows.",
            $"Use one of: {string.Join(", ", ModelCatalog.Providers)}.");
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Is(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
