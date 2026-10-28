using System.Text;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Infrastructure.Configuration;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The process entry point: parse, dispatch, and turn every failure into the documented exit code.
/// </summary>
/// <remarks>
/// The mapping is the product: exit 2 means "read --help", 3 means "export the key", 4 means "the turn
/// hit a budget", 5 means "the transcript is not writable". The agent itself never decides an exit code;
/// it reports an <see cref="SharpCoreAgent.Domain.Agents.AgentReport"/> and this file translates it.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        UseUtf8Output();

        CliOptions options;
        string settingsPath;
        try
        {
            // Resolved once, so `config path`, `config set` and the turn commands are all talking about
            // the same file. Loading is deferred to the commands that read settings: a file this build
            // cannot apply must never be the reason its own repair command refuses to run.
            settingsPath = AgentSettingsFile.PathFromEnvironment();
            options = CliOptions.Parse(args, loadSettings: () => CliSettings.Load(settingsPath));
        }
        catch (CliUsageException ex)
        {
            await WriteUsageErrorAsync(ex).ConfigureAwait(false);
            return ExitCodes.Usage;
        }
        catch (ConfigurationFileException ex)
        {
            await WriteConfigurationErrorAsync(ex).ConfigureAwait(false);
            return ExitCodes.Configuration;
        }
        catch (ArgumentException ex)
        {
            // A settings path the platform refuses to turn into a full path, usually a stray character in
            // SHARPCOREAGENT_CONFIG: the environment is at fault, so this is a usage error.
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        switch (options.Command)
        {
            case CliCommand.Help:
                await Console.Out.WriteLineAsync(HelpText.Usage).ConfigureAwait(false);
                return ExitCodes.Success;

            case CliCommand.Version:
                await Console.Out.WriteLineAsync(HelpText.VersionLine).ConfigureAwait(false);
                return ExitCodes.Success;
        }

        using var scope = new CancellationScope();
        ConsoleCancelEventHandler handler = (_, press) =>
        {
            // Ctrl+C must not kill the process on the first press: the turn has a transcript to save.
            press.Cancel = true;
            scope.HandleCancelKeyPress();
        };

        Console.CancelKeyPress += handler;
        try
        {
            return options.Command switch
            {
                CliCommand.Run => await RunCommand.ExecuteAsync(options, scope.ExitToken).ConfigureAwait(false),

                // `models` reads the same credential store `run` does, so a file it cannot read has to
                // reach the catch below: it used to be dispatched above, where a bad path crashed the
                // process instead of exiting 3.
                CliCommand.Models => ModelsCommand.Execute(options),
                CliCommand.Index => await IndexCommand.ExecuteAsync(options, scope.ExitToken).ConfigureAwait(false),
                CliCommand.Config => ConfigCommand.Execute(
                    options,
                    settingsPath,
                    Environment.GetEnvironmentVariable,
                    Console.Out),
                CliCommand.Setup => SetupCommand.Execute(
                    options,
                    settingsPath,
                    Environment.GetEnvironmentVariable,
                    Console.Out,
                    Console.Error),
                _ => await ChatCommand.ExecuteAsync(options, scope, settingsPath).ConfigureAwait(false),
            };
        }
        catch (CliUsageException ex)
        {
            await WriteUsageErrorAsync(ex).ConfigureAwait(false);
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("cancelled.").ConfigureAwait(false);
            return ExitCodes.Budget;
        }
        catch (CredentialFileException ex)
        {
            // A named file that cannot be read is a credential problem, not a usage problem: the
            // command line was understood, the keys just are not there. Exit 3 is what a script that
            // retries after "log in" already branches on.
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Credentials;
        }
        catch (ConfigurationFileException ex)
        {
            // Reached by the commands that read or write the settings file themselves; parsing already
            // mapped the same failure to the same code.
            await WriteConfigurationErrorAsync(ex).ConfigureAwait(false);
            return ExitCodes.Configuration;
        }
        catch (ModelCredentialException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Credentials;
        }
        catch (ArgumentException ex)
        {
            // ModelCatalog and AgentOptions.Validate reject a bad provider or budget this way: the
            // command line is at fault, so the developer gets a usage error rather than a crash.
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Usage;
        }
        catch (Exception ex)
        {
            await WriteFailureAsync(ex, options.Verbose).ConfigureAwait(false);
            return ExitCodes.Failed;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task WriteUsageErrorAsync(CliUsageException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
        await Console.Error.WriteLineAsync($"hint: {ex.Hint ?? HelpText.Hint}").ConfigureAwait(false);
    }

    /// <summary>
    /// Reports a settings file the process cannot use, with the two commands that fix it.
    /// </summary>
    /// <param name="ex">The failure, whose message already names the file and the key.</param>
    /// <returns>The pending writes.</returns>
    private static async Task WriteConfigurationErrorAsync(ConfigurationFileException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
        await Console.Error.WriteLineAsync(
            "hint: fix the file by hand, or drop one key with 'sharpcoreagent config unset <name>'; 'sharpcoreagent config path' names it.")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Switches the console to UTF-8, because the output rules use <c>→</c>, <c>←</c> and <c>⨯</c>.
    /// </summary>
    /// <remarks>
    /// A Windows console starts on an OEM code page that cannot represent those glyphs, so without this
    /// the arrows arrive as control characters. No BOM is written: the stream is a pipe as often as a
    /// terminal, and a BOM in front of JSON would break every consumer of <c>--json</c>.
    /// </remarks>
    private static void UseUtf8Output()
    {
        if (Console.IsOutputRedirected)
        {
            // A pipe, a file or a test harness owns the stream. Setting the encoding would discard the
            // writer that caller installed -- Console.OutputEncoding re-creates Console.Out -- so the
            // consumer's encoding stays the consumer's choice.
            return;
        }

        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // No console attached (a service host, a closed pipe): the default encoding is all there is.
        }
        catch (System.Security.SecurityException)
        {
            // A sandboxed host may forbid touching the console; readable output still beats a crash.
        }
    }

    private static async Task WriteFailureAsync(Exception ex, bool verbose)
    {
        await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
        if (verbose && ex.StackTrace is not null)
        {
            await Console.Error.WriteLineAsync(ex.StackTrace).ConfigureAwait(false);
        }
    }
}
