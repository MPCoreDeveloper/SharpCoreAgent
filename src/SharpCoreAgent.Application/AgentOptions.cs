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
/// <item><description><see cref="TurnTimeout"/> stops a hung provider from hanging the terminal.</description></item>
/// </list>
/// </remarks>
public sealed record AgentOptions
{
    /// <summary>Gets the maximum number of model round trips in one turn. Default 24.</summary>
    public int MaxIterations { get; init; } = 24;

    /// <summary>Gets the output token ceiling per request. Default 8 000.</summary>
    public int MaxOutputTokensPerTurn { get; init; } = 8_000;

    /// <summary>Gets the session-wide input plus output token ceiling. Default 250 000.</summary>
    public long MaxTotalTokens { get; init; } = 250_000;

    /// <summary>Gets the largest tool result handed back to the model. Default 24 000 characters.</summary>
    public int MaxToolResultChars { get; init; } = 24_000;

    /// <summary>Gets the wall-clock ceiling for one turn. Default ten minutes.</summary>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(10);

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
        Require(MaxIterations, 1, 100, nameof(MaxIterations));
        Require(MaxOutputTokensPerTurn, 256, 64_000, nameof(MaxOutputTokensPerTurn));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalTokens, 1_000);
        Require(MaxToolResultChars, 1_000, 1_000_000, nameof(MaxToolResultChars));
        if (TurnTimeout < TimeSpan.FromSeconds(10) || TurnTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TurnTimeout),
                TurnTimeout,
                "TurnTimeout must be between 10 seconds and 1 hour.");
        }

        if (double.IsNaN(Temperature) || Temperature < 0 || Temperature > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(Temperature), Temperature, "Temperature must be between 0 and 2.");
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
}
