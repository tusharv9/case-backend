namespace CaseManagement.Api.Services;

using CaseManagement.Api.Models;

/// <summary>
/// The working calendar as plain data plus pure arithmetic: which days/hours are working time in which time zone, and which
/// dates are holidays. Everything that needs "business time" — due dates, elapsed time, "is the clock running now" — goes
/// through one of these, so the worker, the dashboard, the notifications and the API can never disagree.
///
/// It has no database access (build one with <see cref="BusinessTimeService"/>), so it can be tested and reused freely.
/// A window is one open/close pair per day; it cannot span midnight.
/// </summary>
public sealed class BusinessCalendar
{
    private const int MaxDaysScanned = 3650;   // 10 years: a guard against a calendar with no working day at all

    private readonly IReadOnlyDictionary<DayOfWeek, (bool Enabled, TimeSpan Start, TimeSpan End)> _week;
    private readonly IReadOnlyDictionary<DateTime, string> _holidays;

    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// A calendar that is always open (UTC, no holidays) — wall-clock time. For callers that run without a configured calendar
    /// (unit tests of unrelated behaviour); the application always supplies the real one.
    /// </summary>
    public static BusinessCalendar RoundTheClock { get; } = new(
        TimeZoneInfo.Utc,
        Enum.GetValues<DayOfWeek>().Select(d => new BusinessHour { DayOfWeek = d, DayName = d.ToString(), IsEnabled = true, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromHours(24) }),
        Array.Empty<(DateTime, string)>());

    public BusinessCalendar(
        TimeZoneInfo timeZone,
        IEnumerable<BusinessHour> hours,
        IEnumerable<(DateTime Date, string Name)> holidays)
    {
        TimeZone = timeZone;

        var week = hours.ToDictionary(h => h.DayOfWeek, h => (h.IsEnabled, h.StartTime, h.EndTime));
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            if (!week.ContainsKey(day))
                throw new InvalidOperationException($"Business hours are not configured for {day}. Configure all seven days under Cases SLA & Routing.");
        }
        _week = week;
        _holidays = holidays.GroupBy(h => h.Date.Date).ToDictionary(g => g.Key, g => g.First().Name);

        if (!_week.Values.Any(d => d.Enabled && d.End > d.Start))
            throw new InvalidOperationException("The business calendar has no working time: enable at least one day with an opening time before its closing time.");
    }

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
    private DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZone);

    private bool TryWindow(DateTime localDate, out TimeSpan start, out TimeSpan end)
    {
        start = end = TimeSpan.Zero;
        if (_holidays.ContainsKey(localDate.Date)) return false;
        var day = _week[localDate.DayOfWeek];
        if (!day.Enabled || day.End <= day.Start) return false;
        start = day.Start;
        end = day.End;
        return true;
    }

    /// <summary>The UTC moment reached by consuming <paramref name="minutes"/> of working time from <paramref name="startUtc"/>.</summary>
    public DateTime AddBusinessMinutes(DateTime startUtc, double minutes)
    {
        if (minutes <= 0) return startUtc;

        var current = ToLocal(startUtc);
        var remaining = minutes;

        for (var scanned = 0; scanned < MaxDaysScanned; scanned++)
        {
            if (!TryWindow(current, out var open, out var close))
            {
                current = current.Date.AddDays(1);
                continue;
            }

            if (current.TimeOfDay < open) current = current.Date + open;           // before opening: the clock starts at opening
            var available = (close - current.TimeOfDay).TotalMinutes;
            if (available <= 0)
            {
                current = current.Date.AddDays(1);                                  // after closing: next day
                continue;
            }

            if (remaining <= available) return ToUtc(current.AddMinutes(remaining));

            remaining -= available;
            current = current.Date.AddDays(1);
        }

        throw new InvalidOperationException("The business calendar has too few working days to reach the requested time.");
    }

    /// <summary>Working minutes between two UTC moments (0 when the range is empty or reversed).</summary>
    public double ElapsedBusinessMinutes(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc) return 0;

        var from = ToLocal(fromUtc);
        var to = ToLocal(toUtc);
        double total = 0;

        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (!TryWindow(day, out var open, out var close)) continue;

            var start = day == from.Date && from.TimeOfDay > open ? from.TimeOfDay : open;
            var end = day == to.Date && to.TimeOfDay < close ? to.TimeOfDay : close;
            if (end > start) total += (end - start).TotalMinutes;
        }

        return total;
    }

    public bool IsWorkingTime(DateTime utc)
    {
        var local = ToLocal(utc);
        return TryWindow(local, out var open, out var close) && local.TimeOfDay >= open && local.TimeOfDay < close;
    }

    /// <summary>The holiday's name if the given moment falls on one, otherwise null.</summary>
    public string? HolidayOn(DateTime utc) => _holidays.TryGetValue(ToLocal(utc).Date, out var name) ? name : null;
}
