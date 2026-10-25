namespace SharpCoreAgent.Cli;

/// <summary>
/// The command line could not be understood.
/// </summary>
/// <remarks>
/// A usage error is the one class of failure that never touches the agent: it is thrown by
/// <see cref="CliOptions.Parse"/>, printed as one line plus a hint, and mapped to exit code 2. Keeping
/// it a distinct type is what stops a typo in <c>--max-iterations</c> from being reported as a failed
/// turn and from being retried by anyone scripting the CLI.
/// </remarks>
internal sealed class CliUsageException : Exception
{
    /// <summary>
    /// Creates a usage error.
    /// </summary>
    /// <param name="message">What is wrong with the command line.</param>
    public CliUsageException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a usage error with a hint.
    /// </summary>
    /// <param name="message">What is wrong with the command line.</param>
    /// <param name="hint">What the developer should try instead.</param>
    public CliUsageException(string message, string hint)
        : base(message) => Hint = hint;

    /// <summary>Gets the hint shown beneath the error, when there is one.</summary>
    public string? Hint { get; }
}
