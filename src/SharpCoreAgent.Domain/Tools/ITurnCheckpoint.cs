namespace SharpCoreAgent.Domain.Tools;

/// <summary>
/// Captures the state of a file before a tool changes it, for the turn's checkpoint.
/// </summary>
/// <remarks>
/// The context carries one of these for the turn that is running. A file-mutating tool calls
/// <see cref="CaptureAsync"/> after it has validated the path and before it writes anything, and refuses the write when
/// the capture reports a problem: a change that cannot be undone has no business being made under a promise of undo.
/// <para>
/// The first capture of a path in a turn wins. A turn that edits the same file three times restores it once, to the
/// state it had before the turn began. Capturing nothing is the documented result of a host that did not enable
/// checkpoints, and every tool keeps working in that case.
/// </para>
/// <para>
/// Disposing the checkpoint closes its turn. A top-level turn is remembered only when its net effect is non-empty; a
/// nested turn (a sub-agent) merges into the turn that invoked it rather than becoming a separate undo step.
/// </para>
/// </remarks>
public interface ITurnCheckpoint : IDisposable
{
    /// <summary>
    /// Captures a file as it is now, unless this turn already captured it.
    /// </summary>
    /// <param name="relativePath">The workspace-relative path, for the developer-facing report.</param>
    /// <param name="fullPath">The absolute path the tool validated and is about to change.</param>
    /// <param name="cancellationToken">Cancels reading the file.</param>
    /// <returns>
    /// <see langword="null"/> when the file is captured or was already captured; otherwise a message explaining why the
    /// change cannot be checkpointed.
    /// </returns>
    ValueTask<string?> CaptureAsync(
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken = default);
}
