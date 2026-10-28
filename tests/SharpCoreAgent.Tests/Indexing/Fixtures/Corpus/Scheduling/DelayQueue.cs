namespace Demo.Scheduling;

/// Keeps pending items in a min-heap ordered by their due timestamp, so the earliest deadline is on top.
public sealed class DelayQueue<T>
{
    private readonly PriorityQueue<T, DateTimeOffset> _due = new();

    public int Count => _due.Count;

    public void Enqueue(T item, DateTimeOffset dueUtc) => _due.Enqueue(item, dueUtc);

    public bool TryDequeue(DateTimeOffset nowUtc, out T? item)
    {
        item = default;
        if (!_due.TryPeek(out _, out var due) || due > nowUtc)
        {
            return false;
        }

        item = _due.Dequeue();
        return true;
    }
}
