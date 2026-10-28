namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>sharpcoreagent setup</c>: the preferences, asked for one at a time.
/// </summary>
/// <remarks>
/// The command exists because <c>config set</c> requires knowing the file syntax, the setting names and the
/// accepted values before the first byte is written. This is the same write through the same validation,
/// with the questions that make those three things unnecessary to remember.
/// </remarks>
internal static class SetupCommand
{
    /// <summary>
    /// Asks the questions and writes the answers.
    /// </summary>
    /// <param name="cli">The parsed command line, which supplies the defaults.</param>
    /// <param name="path">The settings file, absolute.</param>
    /// <param name="readEnvironment">The environment reader, used by the non-interactive fallback table.</param>
    /// <param name="output">Where the questions and the result go.</param>
    /// <param name="error">Where the fallback explanation goes.</param>
    /// <param name="input">Where answers are read; null uses <see cref="Console.In"/>.</param>
    /// <param name="isInteractive">The terminal probe; null uses <see cref="Terminal.Interactive"/>.</param>
    /// <returns>The process exit code.</returns>
    /// <exception cref="SharpCoreAgent.Domain.Errors.ConfigurationFileException">The file cannot be read or written.</exception>
    public static int Execute(
        CliOptions cli,
        string path,
        Func<string, string?> readEnvironment,
        TextWriter output,
        TextWriter error,
        TextReader? input = null,
        Func<bool>? isInteractive = null)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!(isInteractive ?? Terminal.Interactive)())
        {
            // A wizard is a conversation, and a pipe cannot have one. A script gets the same table `config`
            // prints plus the one command that changes a value without asking, so `setup` in CI is
            // informative rather than a hang or a usage error.
            error.WriteLine(
                "setup asks one question at a time, so it needs a terminal on both ends; showing the current settings instead.");
            error.WriteLine("hint: change one with 'sharpcoreagent config set <name> <value>'.");
            return ConfigCommand.Execute(
                cli with { Command = CliCommand.Config, ConfigAction = ConfigAction.Show },
                path,
                readEnvironment,
                output);
        }

        _ = SetupWizard.Run(input ?? Console.In, output, path, cli, out var written);
        if (written > 0)
        {
            output.WriteLine(
                "next: 'sharpcoreagent' starts a session with these values, and 'sharpcoreagent config show' prints them with their source.");
        }

        return ExitCodes.Success;
    }
}
