namespace Demo.Http;

/// Trips after too many consecutive failures, then lets a single probe through before closing again.
public sealed class CircuitBreaker
{
    private readonly int _tripAfter;

    public CircuitBreaker(int tripAfter) => _tripAfter = tripAfter;

    public CircuitState State { get; private set; } = CircuitState.Closed;

    public int ConsecutiveFailures { get; private set; }

    public void Record(bool succeeded, out bool allowProbe)
    {
        ConsecutiveFailures = succeeded ? 0 : ConsecutiveFailures + 1;
        allowProbe = State == CircuitState.HalfOpen;
        if (ConsecutiveFailures >= _tripAfter)
        {
            State = CircuitState.Open;
        }
    }
}

public enum CircuitState
{
    Closed,
    HalfOpen,
    Open,
}
