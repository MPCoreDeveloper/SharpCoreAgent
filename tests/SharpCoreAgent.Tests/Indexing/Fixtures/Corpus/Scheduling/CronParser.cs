using System.Globalization;

namespace Demo.Scheduling;

/// Reads a five-field cron expression and returns the next time it matches.
public sealed class CronParser
{
    private readonly int[] _minutes;
    private readonly int[] _hours;

    public CronParser(string expression)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _minutes = Field(fields[0], 0, 59);
        _hours = Field(fields[1], 0, 23);
    }

    public DateTimeOffset NextOccurrence(DateTimeOffset afterUtc)
    {
        for (var offset = 1; offset <= 24 * 60; offset++)
        {
            var candidate = afterUtc.AddMinutes(offset);
            if (_hours.Contains(candidate.Hour) && _minutes.Contains(candidate.Minute))
            {
                return candidate;
            }
        }

        return afterUtc;
    }

    private static int[] Field(string text, int min, int max) =>
        [.. text.Split(',').Select(value => Math.Clamp(int.Parse(value, CultureInfo.InvariantCulture), min, max))];
}
