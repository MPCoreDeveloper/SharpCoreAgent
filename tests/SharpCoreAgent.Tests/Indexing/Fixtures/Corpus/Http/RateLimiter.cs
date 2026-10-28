namespace Demo.Http;

/// A token bucket: a client may spend at most the configured requests per second, refilled as time passes.
public sealed class RateLimiter
{
    private readonly double _tokensPerSecond;
    private double _available;
    private DateTimeOffset _refilledUtc = DateTimeOffset.UnixEpoch;

    public RateLimiter(double tokensPerSecond, double burst)
    {
        _tokensPerSecond = tokensPerSecond;
        _available = burst;
    }

    public bool TryConsume(DateTimeOffset nowUtc)
    {
        Refill(nowUtc);
        if (_available < 1)
        {
            return false;
        }

        _available -= 1;
        return true;
    }

    private void Refill(DateTimeOffset nowUtc)
    {
        var elapsed = (nowUtc - _refilledUtc).TotalSeconds;
        _available = Math.Min(_available + (elapsed * _tokensPerSecond), _tokensPerSecond);
        _refilledUtc = nowUtc;
    }
}
