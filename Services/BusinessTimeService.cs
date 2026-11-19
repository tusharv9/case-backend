namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Loads the working calendar (the Host's time zone, the weekly hours and holidays) as a <see cref="BusinessCalendar"/>.
/// The time zone is the platform's, managed in the Host (<see cref="IHostTimeZone"/>); the one stored here is only the
/// fallback for when the Host cannot be read.
/// The calendar is cached until configuration changes, so computing a due date or an elapsed time costs no database
/// round-trip. A missing or unusable calendar is a configuration error and fails loudly instead of assuming office hours.
/// </summary>
public class BusinessTimeService : IBusinessTimeService
{
    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;
    private readonly IHostTimeZone? _hostZone;

    public BusinessTimeService(AppDbContext context, IConfigCache cache, IHostTimeZone? hostZone = null)
    {
        _context = context;
        _cache = cache;
        _hostZone = hostZone;
    }

    public static TimeZoneInfo FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("No time zone is set. Set the platform time zone in the Host under Settings → Manage Time Zone.");
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"'{id}' is not a known time zone. Use an IANA name such as 'Asia/Kuala_Lumpur'.");
        }
    }

    public Task<BusinessCalendar> GetCalendarAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync("business-calendar", async () =>
        {
            // The Host's zone; the stored one only when the Host cannot be read (the cache entry lives two minutes, so a
            // change in the Host reaches every SLA clock within that).
            var zoneId = (_hostZone == null ? null : await _hostZone.GetAsync(ct))
                         ?? await _context.BusinessCalendarSettings.AsNoTracking().Select(s => s.TimeZoneId).FirstOrDefaultAsync(ct);
            var hours = await _context.BusinessHours.AsNoTracking().ToListAsync(ct);
            var holidays = await _context.PublicHolidays.AsNoTracking().Where(h => h.IsActive)
                .Select(h => new { h.HolidayDate, h.Name }).ToListAsync(ct);

            return new BusinessCalendar(FindTimeZone(zoneId), hours, holidays.Select(h => (h.HolidayDate, h.Name)));
        });

    public async Task<DateTime> AddBusinessMinutesAsync(DateTime startUtc, int targetMinutes, CancellationToken ct = default) =>
        (await GetCalendarAsync(ct)).AddBusinessMinutes(startUtc, targetMinutes);

    public async Task<int> GetElapsedBusinessMinutesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        (int)Math.Round((await GetCalendarAsync(ct)).ElapsedBusinessMinutes(fromUtc, toUtc));

    public async Task<bool> IsWithinBusinessHoursAsync(DateTime utcTime, CancellationToken ct = default) =>
        (await GetCalendarAsync(ct)).IsWorkingTime(utcTime);

    public async Task<(bool isHoliday, string? holidayName)> GetActiveHolidayAsync(DateTime utcTime, CancellationToken ct = default)
    {
        var name = (await GetCalendarAsync(ct)).HolidayOn(utcTime);
        return (name != null, name);
    }
}
