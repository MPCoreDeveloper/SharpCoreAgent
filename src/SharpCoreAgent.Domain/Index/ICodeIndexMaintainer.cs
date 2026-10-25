namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// Write side of the index: the CLI command and the turn-start refresh, never a tool.
/// </summary>
/// <remarks>
/// Keeping the write path on its own interface is what makes Principle II enforceable: no <c>ITool</c>
/// implementation receives one of these, so no model decision can write to the index. It is also where
/// the writer lock lives — every method here may block on that lock, and none of them throws because
/// another process got there first.
/// </remarks>
public interface ICodeIndexMaintainer
{
    /// <summary>
    /// Brings the index up to date, stopping when the budget runs out.
    /// </summary>
    /// <param name="budget">The wall-clock time the caller is willing to spend, lock wait included.</param>
    /// <param name="cancellationToken">Cancels the work; the index stays usable either way.</param>
    /// <returns>
    /// A report with <see cref="IndexBuildReport.Incomplete"/> set when the workspace was not exhausted, and
    /// with <see cref="IndexStatus.Freshness"/> set to <see cref="IndexFreshness.Building"/> when the wait
    /// for the lock ran out.
    /// </returns>
    ValueTask<IndexBuildReport> EnsureFreshAsync(TimeSpan budget, CancellationToken cancellationToken);

    /// <summary>
    /// Throws the index away and builds it again.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A report describing the new index.</returns>
    ValueTask<IndexBuildReport> RebuildAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Compares the index with the workspace and reports every disagreement.
    /// </summary>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>The verification; a missing index is reported as unhealthy rather than thrown.</returns>
    ValueTask<IndexVerification> VerifyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops rows whose file no longer exists, without touching anything else.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A report whose <see cref="IndexBuildReport.Removed"/> counts the pruned files.</returns>
    ValueTask<IndexBuildReport> PruneAsync(CancellationToken cancellationToken);
}
