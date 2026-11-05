namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The per-connection budget for server sampling requests.
/// </summary>
/// <remarks>
/// A sampling request spends the developer's model budget without the model deciding to, so the limit is checked before
/// the model call and counted even when the model then fails. The window is one wall-clock minute; <see cref="McpSamplingPolicy.First"/>
/// is one request for the lifetime of the connection rather than a rate. The limiter is deliberately separate from the
/// pipe: a restart keeps its history, so a server cannot reset the budget by crashing.
/// </remarks>
internal sealed class McpSamplingLimiter
{
    private readonly McpClientOptions _options;
    private readonly object _gate = new();
    private readonly Queue<(DateTime At, int Input)> _window = new();
    private int _calls;

    public McpSamplingLimiter(McpClientOptions options) => _options = options;

    /// <summary>Gets how many calls this connection has admitted.</summary>
    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    /// <summary>
    /// Tries to admit one request.
    /// </summary>
    /// <param name="inputTokens">The estimated input tokens.</param>
    /// <param name="reason">Why the request is refused, when it is.</param>
    /// <returns><see langword="true"/> when the request may run.</returns>
    public bool TryAcquire(int inputTokens, out string reason)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_options.SamplingPolicy == McpSamplingPolicy.Deny)
            {
                reason = "sampling is denied by --mcp-sampling deny";
                return false;
            }

            if (_options.SamplingPolicy == McpSamplingPolicy.First && _calls > 0)
            {
                reason = "sampling is limited to the first request on this connection";
                return false;
            }

            Prune(now);
            if (_calls >= _options.MaxSamplingCallsPerMinute)
            {
                reason = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"sampling is limited to {_options.MaxSamplingCallsPerMinute} calls per minute");
                return false;
            }

            var inputSpent = _window.Sum(entry => (long)entry.Input);
            if (inputSpent + inputTokens > _options.MaxSamplingInputTokensPerMinute)
            {
                reason = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"sampling is limited to {_options.MaxSamplingInputTokensPerMinute} input tokens per minute");
                return false;
            }

            _calls++;
            _window.Enqueue((now, inputTokens));
            reason = string.Empty;
            return true;
        }
    }

    private void Prune(DateTime now)
    {
        var cutoff = now.AddMinutes(-1);
        while (_window.Count > 0 && _window.Peek().At < cutoff)
        {
            _window.Dequeue();
        }
    }
}
