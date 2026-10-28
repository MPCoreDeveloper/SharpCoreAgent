using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Application;

/// <summary>
/// The budgets and defaults that bound one turn.
/// </summary>
/// <remarks>
/// Every limit here exists because the alternative is an agent that spends a developer's quota while
/// they are not looking. The numbers are deliberately generous for real work and still finite:
/// <list type="bullet">
/// <item><description><see cref="MaxIterations"/> stops a model that keeps calling tools forever;</description></item>
/// <item><description><see cref="MaxTotalTokens"/> stops a session before it costs real money;</description></item>
/// <item><description><see cref="MaxToolResultChars"/> stops one <c>list_directory</c> from filling the context;</description></item>
/// <item><description><see cref="MaxContextMessages"/> keeps a long conversation inside the model's window;</description></item>
/// <item><description><see cref="TurnTimeout"/> stops a hung provider from hanging the terminal.</description></item>
/// </list>
/// <para>
/// The defaults are sized for a working session rather than a demo one. A turn that edits six files and
/// runs the test suite is twenty model calls, not two; a session a developer keeps open all afternoon
/// spends millions of tokens of context, not hundreds of thousands. A ceiling that real work reaches in
/// ten minutes does not protect the developer, it just ends their session — so the defaults here are
/// high enough that reaching one means something unusual happened, and the CLI offers more room instead
/// of ending the conversation (<c>/budget</c>, <c>/continue</c>).
/// </para>
/// </remarks>
public sealed record AgentOptions
{
    /// <summary>Gets the maximum number of model round trips in one turn. Default 400.</summary>
    /// <remarks>
    /// One iteration is one model call, and a call that runs a tool is followed by a call that reads the
    /// result. A turn doing real work — read, edit, build, fix, retest — spends tens of them, so the old
    /// bound of 24 ended ordinary work with "the iteration budget ran out".
    /// </remarks>
    public int MaxIterations { get; init; } = 400;

    /// <summary>Gets the output token ceiling per request. Default 8 000.</summary>
    public int MaxOutputTokensPerTurn { get; init; } = 8_000;

    /// <summary>Gets the session-wide input plus output token ceiling. Default 10 000 000.</summary>
    public long MaxTotalTokens { get; init; } = 10_000_000;

    /// <summary>Gets the largest tool result handed back to the model. Default 24 000 characters.</summary>
    public int MaxToolResultChars { get; init; } = 24_000;

    /// <summary>Gets the transcript size above which the oldest messages are dropped before a request. Default 120.</summary>
    /// <remarks>
    /// The token budget bounds what a session costs; this bounds what one request carries, which is the
    /// other way a long session dies — the provider rejects the request for being too large, or the model
    /// forgets the instruction buried at the top of it. When the transcript exceeds this many messages the
    /// loop drops the oldest ones and leaves a note saying so, keeping the system prompts and the recent
    /// half. Zero disables it, for a host that compacts on its own schedule.
    /// </remarks>
    public int MaxContextMessages { get; init; } = 120;

    /// <summary>Gets the wall-clock ceiling for one turn. Default thirty minutes.</summary>
    /// <remarks>
    /// Before this was raised, a turn that queued a build was cancelled mid-answer with
    /// <see cref="AgentOutcome.BudgetExhausted"/> and a detail blaming the timeout, which read like a bug.
    /// </remarks>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets the sampling temperature. Default 0.2, because code wants less improvising.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Gets the approval mode. Default <see cref="ApprovalMode.Ask"/>.</summary>
    public ApprovalMode Approval { get; init; } = ApprovalMode.Ask;

    /// <summary>Gets the system prompt sent as the first message of every request.</summary>
    public string SystemPrompt { get; init; } = PromptLibrary.SystemPrompt;

    /// <summary>
    /// Checks every limit and returns this instance so it can be used inline.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A limit is outside its documented range.</exception>
    public AgentOptions Validate()
    {
        Require(MaxIterations, 1, 1_000, nameof(MaxIterations));
        Require(MaxOutputTokensPerTurn, 256, 64_000, nameof(MaxOutputTokensPerTurn));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalTokens, 1_000);
        Require(MaxToolResultChars, 1_000, 1_000_000, nameof(MaxToolResultChars));

        // Zero is the documented "do not compact" switch; anything else has to leave a conversation usable, since a
        // window of four messages would keep a single tool round and drop the instruction that asked for it.
        if (MaxContextMessages is not 0)
        {
            Require(MaxContextMessages, 20, 10_000, nameof(MaxContextMessages));
        }

        if (TurnTimeout < TimeSpan.FromSeconds(10) || TurnTimeout > TimeSpan.FromHours(1))
        {
            throw OutOfRange(nameof(TurnTimeout), TurnTimeout, "TurnTimeout must be between 10 seconds and 1 hour.");
        }

        if (double.IsNaN(Temperature) || Temperature < 0 || Temperature > 2)
        {
            throw OutOfRange(nameof(Temperature), Temperature, "Temperature must be between 0 and 2.");
        }

        return this;
    }

    private static void Require(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"must be between {minimum} and {maximum}");
        }
    }

    /// <summary>
    /// Builds the exception for a limit outside its range.
    /// </summary>
    /// <remarks>
    /// The throw goes through a helper so the exception still carries the property name in
    /// <see cref="ArgumentException.ParamName"/> without the analyzer reading that name as an argument
    /// of the method that happened to validate it.
    /// </remarks>
    private static ArgumentOutOfRangeException OutOfRange<T>(string name, T actual, string message) =>
        new(name, actual, message);
}
