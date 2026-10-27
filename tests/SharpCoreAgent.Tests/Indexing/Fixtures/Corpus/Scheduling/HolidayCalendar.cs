namespace Demo.Scheduling;

/// Skips weekends and public holidays when a job counts business days.
public sealed class HolidayCalendar(IEnumerable<DateOnly> holidays)
{
    private readonly HashSet<DateOnly> _holidays = [.. holidays];

    public bool IsBusinessDay(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(day);

    public DateOnly NextBusinessDay(DateOnly day)
    {
        var candidate = day.AddDays(1);
        while (!IsBusinessDay(candidate))
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }
}
