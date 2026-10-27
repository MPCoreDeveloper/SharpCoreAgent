namespace Demo.Telemetry;

/// Decides how many traces are kept: a fixed head sampling ratio, plus everything that carries an error.
public sealed class SamplingPolicy
{
    public SamplingPolicy(double ratio) => Ratio = Math.Clamp(ratio, 0, 1);

    public double Ratio { get; }

    public bool ShouldSample(string traceId, bool isError)
    {
        if (isError)
        {
            return true;
        }

        return Bucket(traceId) < Ratio * 10_000;
    }

    private static uint Bucket(string traceId)
    {
        var hash = 2166136261u;
        foreach (var character in traceId)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash % 10_000;
    }
}
