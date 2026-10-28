using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Infrastructure.Configuration;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The questions <c>sharpcoreagent setup</c> and <c>/config</c> ask, in one place so the two cannot drift.
/// </summary>
/// <remarks>
/// The wizard is deliberately a thin layer over <see cref="CliOptions.WithSetting"/>: every answer is
/// validated by the same parser the equivalent flag uses, so a value accepted here is a value a command
/// line would accept, and the error a developer sees names the same rule. Nothing is written until every
/// question has been answered or the input ends, which is what makes an abandoned run a no-op rather than
/// half a configuration.
/// <para>
/// Answers are merged over the <em>file's</em> document, not over the effective options: a flag typed once
/// is a decision about one run, and turning it into a persisted default without being asked would be the
/// wizard changing more than the developer agreed to.
/// </para>
/// </remarks>
internal static class SetupWizard
{
    /// <summary>
    /// Asks the questions and writes the answers.
    /// </summary>
    /// <param name="input">Where answers are read from, normally <see cref="Console.In"/>.</param>
    /// <param name="output">Where the questions go, normally <see cref="Console.Out"/>.</param>
    /// <param name="path">The settings file to write, absolute.</param>
    /// <param name="current">The options to show as defaults, including anything the session changed.</param>
    /// <param name="written">The number of settings written; zero when nothing changed or the run was abandoned.</param>
    /// <returns>The options the answers describe.</returns>
    /// <exception cref="ConfigurationFileException">The settings file cannot be read or written.</exception>
    public static CliOptions Run(
        TextReader input,
        TextWriter output,
        string path,
        CliOptions current,
        out int written)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(current);

        written = 0;
        var theme = CliHost.ThemeFor(current);
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        var merged = current;

        output.WriteLine(theme.Strong("setup"));
        output.WriteLine(theme.Dim($"Enter keeps the value in brackets; answers are written to {path}."));
        output.WriteLine();

        var (ended, provider) = Ask(
            input, output, theme, merged, "provider", string.Join(", ", ModelCatalog.Providers));
        if (ended)
        {
            return Abandon(output, theme, current);
        }

        merged = Accept(merged, "provider", provider, accepted);

        // The model list follows the provider just chosen, so the developer sees the identifiers that exist
        // rather than the ones that existed on the command line ten seconds ago.
        var models = ModelCatalog.ForProvider(merged.Provider);
        var (modelsEnded, model) = Ask(
            input,
            output,
            theme,
            merged,
            "model",
            string.Join(", ", models.Select(descriptor => descriptor.Id)) + "; Enter keeps the provider default");
        if (modelsEnded)
        {
            return Abandon(output, theme, current);
        }

        merged = Accept(merged, "model", model, accepted);

        var (fileEnded, credentialFile) = Ask(
            input,
            output,
            theme,
            merged,
            "credential-file",
            "a NAME=value file read before the environment");
        if (fileEnded)
        {
            return Abandon(output, theme, current);
        }

        merged = Accept(merged, "credential-file", credentialFile, accepted);
        if (credentialFile is { Length: > 0 } && !File.Exists(credentialFile))
        {
            // Not an error: the developer may be writing the file as they answer, and the read-time error
            // names the path anyway. Saying it now is what turns a typo into a fixable line.
            output.WriteLine($"  {theme.Warning("!")} no file at '{credentialFile}' yet");
        }

        var (approvalEnded, approval) = Ask(
            input, output, theme, merged, "approval", "readonly, ask, allow");
        if (approvalEnded)
        {
            return Abandon(output, theme, current);
        }

        merged = Accept(merged, "approval", approval, accepted);

        var (modeEnded, mode) = Ask(input, output, theme, merged, "mode", "plan, act");
        if (modeEnded)
        {
            return Abandon(output, theme, current);
        }

        merged = Accept(merged, "mode", mode, accepted);

        if (accepted.Count == 0)
        {
            output.WriteLine();
            output.WriteLine(theme.Dim("nothing changed, so nothing was written"));
            return merged;
        }

        Save(path, accepted);
        written = accepted.Count;
        output.WriteLine();
        output.WriteLine($"{theme.Success("✔")} wrote {accepted.Count} setting(s) to {path}");
        return merged;
    }

    /// <summary>
    /// Asks one question until it gets an answer the parser accepts.
    /// </summary>
    /// <param name="input">Where the answer is read from.</param>
    /// <param name="output">Where the question and any complaint go.</param>
    /// <param name="theme">The palette the question is rendered with.</param>
    /// <param name="current">The options the value is validated against.</param>
    /// <param name="name">The canonical setting name.</param>
    /// <param name="hint">What the accepted values are, shown dimmed after the default.</param>
    /// <returns>
    /// Whether the input ended, and the answer: <see langword="null"/> when the developer kept the value.
    /// </returns>
    /// <remarks>
    /// A rejected answer re-asks the same question instead of failing the wizard: the developer is at a
    /// prompt precisely to be told what the parser wants, and a run that stops on the first typo makes
    /// them start over.
    /// </remarks>
    private static (bool Ended, string? Value) Ask(
        TextReader input,
        TextWriter output,
        AnsiTheme theme,
        CliOptions current,
        string name,
        string hint)
    {
        var shown = Current(current, name);
        while (true)
        {
            output.Write($"{theme.Accent(name)} {theme.Dim($"[{shown}]")} {theme.Dim(hint)}: ");
            output.Flush();

            var answer = input.ReadLine();
            if (answer is null)
            {
                output.WriteLine();
                return (true, null);
            }

            var value = answer.Trim();
            if (value.Length == 0)
            {
                return (false, null);
            }

            try
            {
                _ = CliOptions.WithSetting(current, name, value);
                return (false, value);
            }
            catch (CliUsageException ex)
            {
                output.WriteLine($"  {theme.Failure("⨯")} {ex.Message}");
                if (ex.Hint is { Length: > 0 } explanation)
                {
                    output.WriteLine($"    {theme.Dim(explanation)}");
                }
            }
        }
    }

    /// <summary>
    /// Records an answer, or passes the options through when the developer kept the value.
    /// </summary>
    /// <param name="options">The options so far.</param>
    /// <param name="name">The canonical setting name.</param>
    /// <param name="value">The answer, or <see langword="null"/> when nothing was typed.</param>
    /// <param name="accepted">The answers to write.</param>
    /// <returns>The options with the answer applied.</returns>
    private static CliOptions Accept(
        CliOptions options,
        string name,
        string? value,
        Dictionary<string, string> accepted)
    {
        if (value is null)
        {
            return options;
        }

        accepted[name] = value;
        return CliOptions.WithSetting(options, name, value);
    }

    /// <summary>
    /// Renders the value a question is currently defaulting to.
    /// </summary>
    /// <param name="options">The options so far.</param>
    /// <param name="name">The canonical setting name.</param>
    /// <returns>The value as a person would type it.</returns>
    private static string Current(CliOptions options, string name) => name switch
    {
        "provider" => options.Provider,
        "model" => options.Model ?? $"{options.Provider} default",
        "credential-file" => options.CredentialFile ?? "the environment",
        "approval" => options.Approval switch
        {
            ApprovalMode.ReadOnly => "readonly",
            ApprovalMode.AllowAll => "allow",
            _ => "ask",
        },
        "mode" => options.Mode == AgentMode.Plan ? "plan" : "act",
        _ => string.Empty,
    };

    /// <summary>
    /// Writes the answers over the document that is already there.
    /// </summary>
    /// <param name="path">The settings file.</param>
    /// <param name="accepted">The answers, by canonical name.</param>
    /// <exception cref="ConfigurationFileException">The file cannot be read or written.</exception>
    private static void Save(string path, Dictionary<string, string> accepted)
    {
        var document = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in AgentSettingsFile.Read(path))
        {
            document[name] = value;
        }

        foreach (var (name, value) in accepted)
        {
            document[name] = value;
        }

        AgentSettingsFile.Save(path, document);
    }

    /// <summary>
    /// Reports an abandoned run and hands back the options it started with.
    /// </summary>
    /// <param name="output">Where the message goes.</param>
    /// <param name="theme">The palette it is rendered with.</param>
    /// <param name="current">The options before the first question.</param>
    /// <returns><paramref name="current"/>, because nothing was written.</returns>
    private static CliOptions Abandon(TextWriter output, AnsiTheme theme, CliOptions current)
    {
        output.WriteLine();
        output.WriteLine(theme.Warning("cancelled: nothing was written"));
        return current;
    }
}
