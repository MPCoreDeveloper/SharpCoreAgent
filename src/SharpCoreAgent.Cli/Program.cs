using System.Text;
using SharpCoreAgent.Domain.Errors;

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
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException ex)
        {
            WriteUsageError(ex);
            return ExitCodes.Usage;
        }

        switch (options.Command)
        {
            case CliCommand.Help:
                Console.Out.WriteLine(HelpText.Usage);
                return ExitCodes.Success;

            case CliCommand.Version:
                Console.Out.WriteLine($"sharpcoreagent {HelpText.Version}");
                return ExitCodes.Success;

            case CliCommand.Models:
                return ModelsCommand.Execute(options);
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
            return options.Command == CliCommand.Run
                ? await RunCommand.ExecuteAsync(options, scope.ExitToken).ConfigureAwait(false)
                : await ChatCommand.ExecuteAsync(options, scope).ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            WriteUsageError(ex);
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled.");
            return ExitCodes.Budget;
        }
        catch (ModelCredentialException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Credentials;
        }
        catch (ArgumentException ex)
        {
            // ModelCatalog and AgentOptions.Validate reject a bad provider or budget this way: the
            // command line is at fault, so the developer gets a usage error rather than a crash.
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Usage;
        }
        catch (Exception ex)
        {
            WriteFailure(ex, options.Verbose);
            return ExitCodes.Failed;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static void WriteUsageError(CliUsageException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Console.Error.WriteLine($"error: {ex.Message}");
        Console.Error.WriteLine($"hint: {ex.Hint ?? HelpText.Hint}");
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

    private static void WriteFailure(Exception ex, bool verbose)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        if (verbose && ex.StackTrace is not null)
        {
            Console.Error.WriteLine(ex.StackTrace);
        }
    }
}
