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
/// <item><description><see cref="MaxTurnImageBytes"/> stops a turn's pictures from outgrowing the request that carries them;</description></item>
/// <item><description><see cref="MaxContextMessages"/> keeps a long conversation inside the model's window;</description></item>
    /// <item><description><see cref="CompactAtContextFraction"/> compacts before the estimated prompt outgrows the model's window;</description></item>
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

    /// <summary>
    /// Gets the largest total of picture bytes one turn may attach, before base64. Default 16 mebibytes; zero turns
    /// the ceiling off.
    /// </summary>
    /// <remarks>
    /// The three doors that attach an image each bound what they alone return — one picture per call, four per result,
    /// four mebibytes each — and none of them can see the turn. The turn is what a provider charges for, because an
    /// attached picture is re-sent on every request that follows it: four pictures on each of fifteen rounds is sixty
    /// pictures in the last request, more than a provider accepts, and the failure arrives as a status code with no
    /// mention of images. Sixteen mebibytes is twenty-one megabytes of base64, which leaves room inside the smallest
    /// provider request limit this program targets, and it admits four pictures at the per-image ceiling — the worst a
    /// single tool result can produce. A turn showing ordinary screenshots fits dozens.
    /// <para>
    /// The ceiling is per turn and a new turn starts with all of it, which is what makes "ask again next turn" honest
    /// advice rather than a dead end. Zero is the documented switch, for a host that budgets pictures itself.
    /// </para>
    /// </remarks>
    public int MaxTurnImageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the transcript size above which the oldest messages are dropped before a request. Default 120.</summary>
    /// <remarks>
    /// The token budget bounds what a session costs; this bounds what one request carries, which is the
    /// other way a long session dies — the provider rejects the request for being too large, or the model
    /// forgets the instruction buried at the top of it. When the transcript exceeds this many messages the
    /// loop drops the oldest ones and leaves a note saying so, keeping the system prompts and the recent
    /// half. Zero disables it, for a host that compacts on its own schedule.
    /// </remarks>
    public int MaxContextMessages { get; init; } = 120;

    /// <summary>
    /// Gets the fraction of the model's context window above which the loop compacts before a request. Default 0.8;
    /// zero disables the token-based trigger.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxContextMessages"/> is a count and cannot see a single 200 KB tool result; this is the size-aware
    /// companion. The estimate includes the system prompts, the recent transcript and the advertised tool definitions.
    /// The loop keeps the system prompts and compacts the oldest messages until the estimate fits, using the same
    /// tool-round-safe operation as <c>/compact</c>. A model that advertises no context window is never compacted by this
    /// trigger, and the message-count trigger still applies.
    /// </remarks>
    public double CompactAtContextFraction { get; init; } = 0.8;

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

        // Zero is the documented "the host budgets pictures itself" switch. Anything else is a ceiling in bytes, and
        // one byte is a legitimate setting — it means "no picture attaches this turn" — rather than an unusable number,
        // so the floor is one. The ceiling on the ceiling is a provider's own request limit: 64 MiB of decoded picture
        // is 85 MB of base64, which no provider this program targets accepts with text beside it.
        if (MaxTurnImageBytes is not 0)
        {
            Require(MaxTurnImageBytes, 1, 64 * 1024 * 1024, nameof(MaxTurnImageBytes));
        }

        // Zero is the documented "do not compact" switch; anything else has to leave a conversation usable, since a
        // window of four messages would keep a single tool round and drop the instruction that asked for it.
        if (MaxContextMessages is not 0)
        {
            Require(MaxContextMessages, 20, 10_000, nameof(MaxContextMessages));
        }

        // Zero is the documented "do not compact by size" switch. A fraction below ten percent would compact
        // almost every ordinary request; above one would never trigger.
        if (CompactAtContextFraction is not 0
            && (double.IsNaN(CompactAtContextFraction) || CompactAtContextFraction < 0.1 || CompactAtContextFraction > 1))
        {
            throw OutOfRange(
                nameof(CompactAtContextFraction),
                CompactAtContextFraction,
                "CompactAtContextFraction must be 0 or between 0.1 and 1.");
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
