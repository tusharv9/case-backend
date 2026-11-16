namespace CaseManagement.Api.Models;

/// <summary>
/// The one row that says which time zone the business-hours schedule is written in. Working hours ("09:00–17:00") only mean
/// something relative to a zone, so it is configuration — not a constant in the code. It also holds how long a ticket without a
/// priority may wait to be assigned.
/// </summary>
public class BusinessCalendarSetting : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>An IANA zone id such as "Asia/Kuala_Lumpur" or "Europe/London".</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>The assign-within target of a ticket with no priority (or one not configured). 0 = no target.</summary>
    public int TicketDefaultAssignValue { get; set; } = 30;
    public string TicketDefaultAssignUnit { get; set; } = "Minutes"; // "Minutes" | "Hours"
    public int TicketDefaultAssignMinutes { get; set; } = 30;
}
