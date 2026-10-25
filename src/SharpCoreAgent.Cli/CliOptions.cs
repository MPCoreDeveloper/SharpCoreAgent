using System.Globalization;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Agents;
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

    /// <summary>Gets the approval mode.</summary>
    public ApprovalMode Approval { get; init; } = ApprovalMode.Ask;

    /// <summary>Gets the iteration ceiling.</summary>
    public int MaxIterations { get; init; } = 24;

    /// <summary>Gets the per-request output ceiling.</summary>
    public int MaxOutputTokens { get; init; } = 8_000;

    /// <summary>Gets the session token budget.</summary>
    public long MaxTotalTokens { get; init; } = 250_000;

    /// <summary>Gets the sampling temperature.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Gets the endpoint for the <c>openai-compatible</c> provider.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Gets the transcript path, or null for the timestamped default.</summary>
    public string? SessionPath { get; init; }

    /// <summary>Gets a value indicating whether a transcript should be written.</summary>
    public bool SaveSession { get; init; } = true;

    /// <summary>Gets a value indicating whether events are emitted as JSON lines.</summary>
    public bool Json { get; init; }

    /// <summary>Gets a value indicating whether thinking and tool summaries are suppressed.</summary>
    public bool Quiet { get; init; }

    /// <summary>Gets a value indicating whether diagnostics are shown.</summary>
    public bool Verbose { get; init; }

    /// <summary>Projects the command line onto the container's options.</summary>
    /// <returns>The options <c>AddSharpCoreAgent</c> consumes.</returns>
    public SharpCoreAgentOptions ToSharpCoreAgentOptions() => new()
    {
        Provider = Provider,
        ModelId = Model,
        WorkspaceRoot = Workspace,
        Approval = Approval,
        MaxIterations = MaxIterations,
        MaxOutputTokensPerTurn = MaxOutputTokens,
        MaxTotalTokens = MaxTotalTokens,
        Temperature = Temperature,
        BaseUrl = BaseUrl,
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
    /// <returns>The parsed options.</returns>
    /// <exception cref="CliUsageException">An argument is unknown, incomplete or contradictory.</exception>
    public static CliOptions Parse(string[] args, Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = FromEnvironment(readEnvironment ?? Environment.GetEnvironmentVariable);

        if (args.Length == 0)
        {
            return options with { Command = CliCommand.Help };
        }

        return ScanArguments(args, options);
    }

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
    };

    /// <summary>
    /// Walks the arguments, applying options and collecting the command and its positionals.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <param name="options">The options to start from.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="CliUsageException">An argument is unknown or incomplete.</exception>
    private static CliOptions ScanArguments(string[] args, CliOptions options)
    {
        CliCommand? command = null;
        var positionals = new List<string>();
        var optionsEnded = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (!optionsEnded && TryParseFlag(argument, out var flag))
            {
                return options with { Command = flag };
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

        return Finish(options, command, positionals);
    }

    /// <summary>
    /// Turns the collected command and positionals into validated options.
    /// </summary>
    /// <param name="options">The options the flags produced.</param>
    /// <param name="command">The command, or <see langword="null"/> when none was named.</param>
    /// <param name="positionals">The arguments that were not options.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="CliUsageException">The command and its arguments contradict each other.</exception>
    private static CliOptions Finish(CliOptions options, CliCommand? command, List<string> positionals)
    {
        switch (command)
        {
            case null:
            case CliCommand.Help:
            case CliCommand.Version:
                return options with { Command = command ?? CliCommand.Help };

            case CliCommand.Run:
                var prompt = string.Join(' ', positionals).Trim();
                if (prompt.Length == 0)
                {
                    throw new CliUsageException(
                        "run needs a prompt.",
                        "For example: sharpcoreagent run \"list the files in src\"");
                }

                return options with { Command = CliCommand.Run, Prompt = prompt };

            case CliCommand.Chat:
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

            default:
                if (positionals.Count > 0)
                {
                    throw new CliUsageException($"models does not take '{positionals[0]}'.");
                }

                return options with { Command = CliCommand.Models };
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

            case "--session":
                return options with { SessionPath = TakeValue(args, ref index, name), SaveSession = true };

            case "--no-session":
                return options with { SessionPath = null, SaveSession = false };

            case "--json":
                return options with { Json = true };

            case "--quiet":
                return options with { Quiet = true };

            case "--verbose":
                return options with { Verbose = true };

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
            "Commands are run, chat and models.");
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
