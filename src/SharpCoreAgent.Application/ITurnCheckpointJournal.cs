using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// The process-local history of turn checkpoints and the operation that restores the latest one.
/// </summary>
/// <remarks>
/// The journal begins a checkpoint for every turn, including read-only and failed turns. A checkpoint with no net
/// change is discarded at completion, so <see cref="UndoLast"/> always answers about a turn that actually changed
/// a file. History is memory, not transcript state: a resumed session starts with nothing to undo.
/// <para>
/// The implementation is in Infrastructure because restoring is file-system work; the loop and the CLI depend on this
/// contract so neither learns how a snapshot is stored.
/// </para>
/// </remarks>
public interface ITurnCheckpointJournal
{
    /// <summary>Opens a checkpoint for one turn.</summary>
    /// <returns>The checkpoint the turn passes to its tools; disposing it closes the turn.</returns>
    ITurnCheckpoint BeginTurn();

    /// <summary>Restores the most recent completed checkpoint that changed a file.</summary>
    /// <returns>
    /// What was restored, or a result whose <see cref="CheckpointUndo.HadCheckpoint"/> is false when there is no
    /// checkpoint. A failed restore leaves the checkpoint in place so the operation can be retried.
    /// </returns>
    CheckpointUndo UndoLast();
}

/// <summary>The result of one <c>/undo</c>.</summary>
public sealed record CheckpointUndo
{
    /// <summary>Gets a value indicating whether a checkpoint was available.</summary>
    public bool HadCheckpoint { get; init; }

    /// <summary>Gets the workspace-relative files restored to content they had before the turn.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];

    /// <summary>Gets the workspace-relative files removed because the turn created them.</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>Gets the files that could not be restored and why.</summary>
    public IReadOnlyList<CheckpointFailure> Failures { get; init; } = [];

    /// <summary>Gets a value indicating whether the checkpoint was fully restored and consumed.</summary>
    public bool Succeeded => HadCheckpoint && Failures.Count == 0;
}

/// <summary>One file that could not be restored.</summary>
/// <param name="Path">The workspace-relative path.</param>
/// <param name="Reason">Why the file was left alone.</param>
public sealed record CheckpointFailure(string Path, string Reason);
