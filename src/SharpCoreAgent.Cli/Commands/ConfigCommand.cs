using System.Globalization;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Configuration;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>sharpcoreagent config</c>: the settings this developer keeps, where they are kept, and which
/// layer each effective value came from.
/// </summary>
/// <remarks>
/// The command is deliberately boring about secrets: it reports whether the active provider's key is
/// configured and which name supplied it, and it refuses to write one into the settings file. A
/// preferences document is copied, committed and pasted into chats; a key belongs in the file
/// <c>--credential-file</c> names, or in the environment.
/// <para>
/// The three verbs that write or locate the file read it as a <em>document</em> only, so an
/// unresolvable value (<c>"approval": "sometimes"</c>) never stops the developer from repairing it.
/// </para>
/// </remarks>
internal static class ConfigCommand
{
    /// <summary>
    /// Runs the verb the command line asked for.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="path">The settings file, absolute.</param>
    /// <param name="readEnvironment">The environment reader, which explains part of the provenance.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <returns>The process exit code.</returns>
    /// <exception cref="SharpCoreAgent.Domain.Errors.ConfigurationFileException">The file cannot be read or written.</exception>
    public static int Execute(
        CliOptions cli,
        string path,
        Func<string, string?> readEnvironment,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(output);

        return cli.ConfigAction switch
        {
            ConfigAction.Get => Get(cli, output),
            ConfigAction.Set => Set(cli, path, output),
            ConfigAction.Unset => Unset(cli, path, output),
            ConfigAction.Path => WritePath(path, output),
            _ => Show(cli, path, readEnvironment, output),
        };
    }

    /// <summary>
    /// Prints every setting with its value and the layer it came from.
    /// </summary>
    /// <param name="cli">The merged options.</param>
    /// <param name="path">The settings file, absolute.</param>
    /// <param name="readEnvironment">The environment reader.</param>
    /// <param name="output">Where the table goes.</param>
    /// <returns>The exit code.</returns>
    private static int Show(CliOptions cli, string path, Func<string, string?> readEnvironment, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var document = AgentSettingsFile.Read(path);
        var theme = CliHost.ThemeFor(cli);
        var width = CliOptions.SettableNames.Max(name => name.Length) + 2;

        foreach (var name in CliOptions.SettableNames)
        {
            output.WriteLine(string.Concat(
                name.PadRight(width),
                Value(cli, name).PadRight(34),
                theme.Dim(Source(cli, name, document, readEnvironment))));
        }

        output.WriteLine(string.Concat(theme.Dim("credential".PadRight(width)), Credential(cli, theme)));
        output.WriteLine(string.Concat(
            theme.Dim("config file".PadRight(width)),
            path,
            File.Exists(path) ? string.Empty : theme.Dim("  (not created yet)")));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Prints one value and nothing else, so a script can substitute it.
    /// </summary>
    /// <param name="cli">The merged options.</param>
    /// <param name="output">Where the value goes.</param>
    /// <returns>The exit code.</returns>
    private static int Get(CliOptions cli, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(Value(cli, cli.ConfigName ?? string.Empty));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Writes one value, after validating it the way its flag would.
    /// </summary>
    /// <param name="cli">The parsed command line, without the settings file.</param>
    /// <param name="path">The settings file, absolute.</param>
    /// <param name="output">Where the confirmation goes.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="CliUsageException">The value is one the equivalent flag would reject.</exception>
    private static int Set(CliOptions cli, string path, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var name = cli.ConfigName ?? string.Empty;
        var value = cli.ConfigValue ?? string.Empty;
        var candidate = CliOptions.WithSetting(cli, name, value);
        EnsureRunnable(candidate, name, value);

        var settings = new Dictionary<string, string>(AgentSettingsFile.Read(path), StringComparer.Ordinal);
        RemoveMatching(settings, name);
        settings[name] = value;
        AgentSettingsFile.Save(path, settings);

        output.WriteLine($"set {name} in {path}");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Removes one value, so its built-in default applies again.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <param name="path">The settings file, absolute.</param>
    /// <param name="output">Where the confirmation goes.</param>
    /// <returns>The exit code.</returns>
    private static int Unset(CliOptions cli, string path, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var name = cli.ConfigName ?? string.Empty;
        var settings = new Dictionary<string, string>(AgentSettingsFile.Read(path), StringComparer.Ordinal);
        if (!RemoveMatching(settings, name))
        {
            output.WriteLine($"{name} is not set in {path}");
            return ExitCodes.Success;
        }

        AgentSettingsFile.Save(path, settings);
        output.WriteLine($"removed {name} from {path}");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Removes every key that means the setting, whatever its spelling.
    /// </summary>
    /// <param name="settings">The document to change.</param>
    /// <param name="name">The setting to remove.</param>
    /// <returns><see langword="true"/> when a key was removed.</returns>
    /// <remarks>
    /// A name that is not a setting still matches itself, so a key a newer build wrote — or a typo the
    /// developer wants gone — can be removed with the tool instead of by hand. Matching is folded
    /// because <c>maxIterations</c> and <c>max-iterations</c> are the same setting, and leaving both in
    /// the document would leave the next <c>config show</c> to pick one.
    /// </remarks>
    private static bool RemoveMatching(Dictionary<string, string> settings, string name)
    {
        var canonical = CliOptions.CanonicalName(name);
        var doomed = settings.Keys
            .Where(key => canonical is not null
                ? string.Equals(CliOptions.CanonicalName(key), canonical, StringComparison.Ordinal)
                : CliOptions.IsSameName(key, name))
            .ToList();

        foreach (var key in doomed)
        {
            settings.Remove(key);
        }

        return doomed.Count > 0;
    }

    /// <summary>
    /// Refuses a value that parses but cannot describe a session.
    /// </summary>
    /// <param name="candidate">The options with the new value in place.</param>
    /// <param name="name">The setting, for the message.</param>
    /// <param name="value">The value, for the message.</param>
    /// <exception cref="CliUsageException">A limit is outside its range.</exception>
    /// <remarks>
    /// Without this, <c>config set temperature 5</c> would be written and then break every command that
    /// reads the file — including the ones that would explain why.
    /// </remarks>
    private static void EnsureRunnable(CliOptions candidate, string name, string value)
    {
        try
        {
            candidate.ToSharpCoreAgentOptions().ToAgentOptions();
        }
        catch (ArgumentException ex)
        {
            throw new CliUsageException($"'{value}' is not a value for {name}.", ex.Message);
        }
    }

    /// <summary>
    /// Prints the file in use.
    /// </summary>
    /// <param name="path">The settings file, absolute.</param>
    /// <param name="output">Where the path goes.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// One plain line, no colour and no label: this is the value a script substitutes into an editor
    /// command or a backup list.
    /// </remarks>
    private static int WritePath(string path, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(path);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Renders one effective value.
    /// </summary>
    /// <param name="cli">The merged options.</param>
    /// <param name="name">The canonical setting name.</param>
    /// <returns>The value as a person reads it.</returns>
    private static string Value(CliOptions cli, string name) => name switch
    {
        "provider" => cli.Provider,
        "model" => cli.Model ?? "provider default",
        "base-url" => cli.BaseUrl ?? "none",
        "approval" => cli.Approval switch
        {
            ApprovalMode.ReadOnly => "readonly",
            ApprovalMode.AllowAll => "allow",
            _ => "ask",
        },
        "mode" => cli.Mode == AgentMode.Plan ? "plan" : "act",
        "temperature" => cli.Temperature.ToString("0.###", CultureInfo.InvariantCulture),
        "max-iterations" => cli.MaxIterations.ToString(CultureInfo.InvariantCulture),
        "max-output-tokens" => cli.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
        "max-total-tokens" => cli.MaxTotalTokens.ToString(CultureInfo.InvariantCulture),
        "credential-file" => cli.CredentialFile ?? "none",
        _ => string.Empty,
    };

    /// <summary>
    /// Names the layer a value came from.
    /// </summary>
    /// <param name="cli">The merged options, which know what the arguments said.</param>
    /// <param name="name">The canonical setting name.</param>
    /// <param name="document">The settings file, as read.</param>
    /// <param name="readEnvironment">The environment reader.</param>
    /// <returns><c>command line</c>, <c>environment</c>, <c>file</c> or <c>default</c>.</returns>
    private static string Source(
        CliOptions cli,
        string name,
        IReadOnlyDictionary<string, string> document,
        Func<string, string?> readEnvironment)
    {
        if (cli.CommandLineSettings.Contains(name))
        {
            return "command line";
        }

        if (CliOptions.EnvironmentVariableFor(name) is { } variable
            && !string.IsNullOrWhiteSpace(readEnvironment(variable)))
        {
            return "environment";
        }

        return document.Keys.Any(key => string.Equals(CliOptions.CanonicalName(key), name, StringComparison.Ordinal))
            ? "file"
            : "default";
    }

    /// <summary>
    /// Reports whether the active provider has a key, and which name supplied it.
    /// </summary>
    /// <param name="cli">The merged options, for the provider and the credential file.</param>
    /// <param name="theme">The palette the warning is rendered with.</param>
    /// <returns>One line, never containing a value.</returns>
    private static string Credential(CliOptions cli, AnsiTheme theme)
    {
        var names = ModelCatalog.CredentialNames(cli.Provider);
        if (names.Count == 0)
        {
            return theme.Dim("not needed for this provider");
        }

        var store = EnvironmentCredentialStore.FromFile(cli.CredentialFile);
        var found = names.FirstOrDefault(store.IsConfigured);
        return found is null
            ? theme.Warning($"missing ({string.Join(" or ", names)})")
            : $"set ({found})";
    }
}
