using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services.Calendar;
using Microsoft.AspNetCore.Mvc;

namespace CaseManagement.Api.Controllers;

/// <summary>The Case Management Calendar's events for a date range (at most two months), within the cases the caller may see.</summary>
[ApiController]
[Route("api/calendar")]
[RequirePermission(Permissions.CasesRead)]
public class CalendarController(ICaseCalendarService calendar) : BaseApiController
{
    /// <param name="from">Start of the range (UTC, inclusive).</param>
    /// <param name="to">End of the range (UTC, exclusive).</param>
    /// <param name="types">Comma-separated: created, slaDue, firstResponseDue, meeting, pauseEnds. Empty = all.</param>
    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] string? types, [FromQuery] Guid? teamId,
        [FromQuery] Guid? ownerId, [FromQuery] string? status, [FromQuery] string? priority, [FromQuery] bool mine, CancellationToken ct)
    {
        var list = string.IsNullOrWhiteSpace(types) ? null : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Ok(await calendar.GetAsync(new CalendarQuery(DateTime.SpecifyKind(from.ToUniversalTime(), DateTimeKind.Utc), DateTime.SpecifyKind(to.ToUniversalTime(), DateTimeKind.Utc),
            list, teamId, ownerId, status, priority, mine), ct));
    }
}
