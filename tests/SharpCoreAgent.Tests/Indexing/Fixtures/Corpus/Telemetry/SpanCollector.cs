namespace Demo.Telemetry;

/// Collects trace spans and nests each one under the span that was active when it started.
public sealed class SpanCollector
{
    private readonly List<Span> _spans = [];
    private readonly AsyncLocal<string?> _current = new();

    public IReadOnlyList<Span> Spans => _spans;

    public IDisposable Begin(string name)
    {
        var span = new Span(name, _current.Value, DateTimeOffset.UtcNow);
        _spans.Add(span);
        _current.Value = span.Id;
        return new Scope(this, span);
    }

    private sealed class Scope(SpanCollector collector, Span span) : IDisposable
    {
        public void Dispose()
        {
            span.Duration = DateTimeOffset.UtcNow - span.StartedUtc;
            collector._current.Value = span.ParentId;
        }
    }
}
