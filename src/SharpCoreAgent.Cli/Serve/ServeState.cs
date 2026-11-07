namespace SharpCoreAgent.Cli;

/// <summary>
/// What the page's footer shows and what <c>GET /api/state</c> answers.
/// </summary>
/// <param name="Model">The <c>provider:model</c> the next turn runs on.</param>
/// <param name="Approval">The approval mode, as the word the commands print.</param>
/// <param name="Mode">The agent mode: <c>plan</c> or <c>act</c>.</param>
/// <param name="TokensSpent">The session's tokens so far.</param>
/// <param name="Ceiling">The session's ceiling; <see cref="long.MaxValue"/> means unlimited.</param>
internal sealed record ServeState(string Model, string Approval, string Mode, long TokensSpent, long Ceiling);
