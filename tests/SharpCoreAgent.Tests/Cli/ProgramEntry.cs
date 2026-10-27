using System.Reflection;
using SharpCoreAgent.Cli;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// Invokes <see cref="Program"/>'s entry point the way a shell does.
/// </summary>
/// <remarks>
/// Through reflection because <c>Main</c> is deliberately not public: a test that called a friendlier
/// overload would not be exercising the dispatch and exit-code mapping that scripts depend on. The
/// caller owns the <see cref="ConsoleScope"/>, so the assertions can read what the process printed.
/// </remarks>
internal static class ProgramEntry
{
    /// <summary>
    /// Runs one command line.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var main = typeof(Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Program.Main was not found.");

        return await ((Task<int>)main.Invoke(null, [args])!).ConfigureAwait(false);
    }
}
