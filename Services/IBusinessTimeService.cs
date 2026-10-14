namespace CaseManagement.Api.Services;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IBusinessTimeService
{
    /// <summary>The configured working calendar (cached until configuration changes).</summary>
    Task<BusinessCalendar> GetCalendarAsync(CancellationToken ct = default);

    /// <summary>
    /// Calculates a target deadline in UTC by adding business minutes to startUtc,
    /// strictly adhering to configured business hours and pausing for non-working days and public holidays.
    /// </summary>
    Task<DateTime> AddBusinessMinutesAsync(DateTime startUtc, int targetMinutes, CancellationToken ct = default);

    /// <summary>
    /// Calculates the number of business minutes elapsed between fromUtc and toUtc,
    /// counting only active business hours on non-holiday business days.
    /// </summary>
    Task<int> GetElapsedBusinessMinutesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the given UTC timestamp falls within an active business hour window.
    /// </summary>
    Task<bool> IsWithinBusinessHoursAsync(DateTime utcTime, CancellationToken ct = default);

    /// <summary>
    /// Checks if the given UTC timestamp falls on an active registered public holiday.
    /// Returns (true, holidayName) if active holiday, else (false, null).
    /// </summary>
    Task<(bool isHoliday, string? holidayName)> GetActiveHolidayAsync(DateTime utcTime, CancellationToken ct = default);
}
