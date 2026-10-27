namespace Demo.Telemetry;

/// Keeps the recorded durations of one metric and answers its median and 95th percentile.
public sealed class MetricAggregator
{
    private readonly List<double> _durations = [];

    public int Count => _durations.Count;

    public void Record(double milliseconds) => _durations.Add(milliseconds);

    public double Percentile(double percentile)
    {
        if (_durations.Count == 0)
        {
            return 0;
        }

        var ordered = _durations.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * ordered.Length) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }
}
