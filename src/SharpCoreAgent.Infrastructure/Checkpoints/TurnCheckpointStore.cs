using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Checkpoints;

/// <summary>
/// Keeps the pre-turn file states needed by <c>/undo</c>, one checkpoint per turn.
/// </summary>
/// <remarks>
/// Snapshots are held in memory for the life of the process. A checkpoint remembers the first state each file had in
/// its turn, merges a nested sub-agent turn into the turn that invoked it, and discards a turn whose net effect is
/// empty. A top-level turn that changed something is pushed only when it finishes, so undo is LIFO and a read-only
/// review never hides an earlier edit.
/// <para>
/// One file is capped at <see cref="MaxCapturedFileBytes"/>. A larger file is refused by the mutating tool before it is
/// changed, because a checkpoint that silently omitted the bytes could not keep the promise <c>/undo</c> makes.
/// </para>
/// </remarks>
public sealed partial class TurnCheckpointStore : ITurnCheckpointJournal
{
    /// <summary>Gets the largest file one checkpoint can capture, in bytes.</summary>
    public const long MaxCapturedFileBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Stack<TurnCheckpoint> _history = [];
    private readonly AsyncLocal<TurnCheckpoint?> _current = new();

    /// <inheritdoc />
    public ITurnCheckpoint BeginTurn()
    {
        var turn = new TurnCheckpoint(this, _current.Value);
        _current.Value = turn;
        return turn;
    }

    /// <inheritdoc />
    public CheckpointUndo UndoLast()
    {
        lock (_gate)
        {
            if (_history.Count == 0)
            {
                return new CheckpointUndo();
            }

            var turn = _history.Peek();
            var undo = turn.Restore(CancellationToken.None);
            if (undo.Failures.Count == 0)
            {
                _history.Pop();
            }

            return undo;
        }
    }

    private void Complete(TurnCheckpoint turn)
    {
        _current.Value = turn.Parent;
        if (turn.Parent is { } parent)
        {
            parent.Merge(turn);
            return;
        }

        if (!turn.Seal())
        {
            return;
        }

        lock (_gate)
        {
            _history.Push(turn);
        }
    }
}
