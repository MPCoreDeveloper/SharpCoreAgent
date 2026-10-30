namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// Why a code-intelligence call could not answer.
/// </summary>
/// <remarks>
/// A failure is a value, not an exception. "There is no project here", "the references are not restored" and
/// "nothing is at that position" are ordinary answers a model can act on, and the tool turns one into the
/// instruction that unblocks it. <see cref="Hint"/> names a tool this repository actually ships, because a hint
/// suggesting a command the agent does not have is worse than no hint at all.
/// </remarks>
public sealed record CodeFailure
{
    /// <summary>
    /// Gets the stable failure code, for example <c>FSH001</c>.
    /// </summary>
    /// <remarks>
    /// A test asserts on the code, never on the message, so the message stays free to improve.
    /// </remarks>
    public required string Code { get; init; }

    /// <summary>
    /// Gets what went wrong, in one sentence.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the next thing to try, or <see langword="null"/> when there is nothing honest to suggest.
    /// </summary>
    public string? Hint { get; init; }
}
