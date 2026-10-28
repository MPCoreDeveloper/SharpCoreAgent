namespace SharpCoreAgent.Cli;

/// <summary>
/// What the process can learn about the terminal it was started from.
/// </summary>
/// <remarks>
/// One question is asked of a console at startup: can the developer type into this process? A REPL
/// started where nothing can be typed into it blocks a script forever, so the answer decides whether a
/// bare command line means "talk to me" or "explain yourself".
/// <para>
/// <see cref="Interactive"/> is settable because a test host cannot allocate a terminal and must still
/// describe one; it is the only mutable static in the CLI, and the tests that change it put it back.
/// </para>
/// </remarks>
internal static class Terminal
{
    /// <summary>Gets or sets the probe that answers whether a keyboard is attached.</summary>
    internal static Func<bool> Interactive { get; set; } = Default;

    /// <summary>
    /// Reads the process's own console.
    /// </summary>
    /// <returns><see langword="true"/> when both standard streams are terminals.</returns>
    /// <remarks>
    /// Both, because a session needs a keyboard to be typed into and a screen to be read from. A pipe
    /// on either side means a script, a CI step or a redirect, and all three want the usage block rather
    /// than a prompt nobody will answer. The conservative answer is also the one that cannot hang.
    /// </remarks>
    private static bool Default() => !Console.IsInputRedirected && !Console.IsOutputRedirected;
}
