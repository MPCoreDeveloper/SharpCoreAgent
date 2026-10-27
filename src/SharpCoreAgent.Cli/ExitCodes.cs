using SharpCoreAgent.Domain.Agents;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The process exit codes of <c>contracts/cli.md</c>.
/// </summary>
/// <remarks>
/// The numbers are part of the contract because scripts branch on them, so they are named here once and
/// never inlined. A successful <c>chat</c> session always exits <see cref="Success"/>, even after a failed
/// turn: the conversation itself was the work, and a developer who quit normally did not ask for a
/// failure code.
/// </remarks>
internal static class ExitCodes
{
    /// <summary>The turn completed and the session was saved.</summary>
    public const int Success = 0;

    /// <summary>The turn failed: model error, tool crash or an unexpected exception.</summary>
    public const int Failed = 1;

    /// <summary>The command line could not be understood.</summary>
    public const int Usage = 2;

    /// <summary>A provider credential is missing.</summary>
    public const int Credentials = 3;

    /// <summary>A budget ran out, or the developer interrupted the turn.</summary>
    public const int Budget = 4;

    /// <summary>The transcript could not be read or written.</summary>
    public const int SessionIo = 5;

    /// <summary>The index could not be created, written or read.</summary>
    /// <remarks>
    /// The one code this feature adds, and the one a script needs to tell apart from a failed build: retrying a
    /// permissions problem or a full disk is pointless, while retrying a lock conflict is exactly right.
    /// </remarks>
    public const int IndexUnavailable = 6;

    /// <summary>
    /// Maps a turn's outcome onto its exit code.
    /// </summary>
    /// <param name="report">The finished turn.</param>
    /// <returns>The exit code for the outcome.</returns>
    public static int ForReport(AgentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Outcome switch
        {
            AgentOutcome.Completed => Success,
            AgentOutcome.BudgetExhausted or AgentOutcome.Cancelled => Budget,
            _ => Failed,
        };
    }
}
