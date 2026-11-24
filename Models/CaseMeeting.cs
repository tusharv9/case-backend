namespace CaseManagement.Api.Models;

public static class CaseMeetingStatuses
{
    public const string Scheduled = "Scheduled";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// A meeting with the customer, booked from a case's Workflow Timeline. Its invitation goes to the customer by email (through the
/// case's mailbox, with a calendar invite their mail program can add), it shows on the case's timeline and on the Calendar.
/// </summary>
public class CaseMeeting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Case? Case { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>The platform time zone the meeting was booked in (shown with the times).</summary>
    public string TimeZone { get; set; } = "UTC";
    public string? Agenda { get; set; }
    public string? JoinUrl { get; set; }
    /// <summary>Where the link came from: "Manual" (pasted by the agent). Automatic providers (Teams, Google Meet) come later.</summary>
    public string Provider { get; set; } = "Manual";
    public string? ProviderMeetingId { get; set; }
    public Guid OrganizerUserId { get; set; }
    public User? Organizer { get; set; }
    public string AttendeeEmail { get; set; } = string.Empty;
    public string? AttendeeName { get; set; }
    /// <summary><see cref="CaseMeetingStatuses"/>.</summary>
    public string Status { get; set; } = CaseMeetingStatuses.Scheduled;
    /// <summary>The calendar invite's identity and revision: an update or cancellation replaces the same entry in the customer's calendar.</summary>
    public string IcsUid { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public int Version { get; set; }
}
