namespace Demo.Telemetry;

/// Buffers log lines and flushes them to the file on an interval, so a busy loop does not block on I/O.
public sealed class LogSink : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(2);
    private DateTimeOffset _flushedUtc = DateTimeOffset.UtcNow;

    public LogSink(string path) => _writer = new StreamWriter(path) { AutoFlush = false };

    public async Task WriteAsync(string line, CancellationToken cancellationToken)
    {
        await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        if (DateTimeOffset.UtcNow - _flushedUtc < _flushInterval)
        {
            return;
        }

        await _writer.FlushAsync(cancellationToken);
        _flushedUtc = DateTimeOffset.UtcNow;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync();
        await _writer.DisposeAsync();
    }
}
