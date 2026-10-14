namespace CaseManagement.Api.Models;

public class NotificationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipientUserId { get; set; }
    public User? RecipientUser { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Guid? CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public bool IsRead { get; set; } = false;
    public string Priority { get; set; } = "High";
    public int ReminderCount { get; set; } = 0;
    public DateTime? LastReminderAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }

    /// <summary>Identifies the event this notification reports (e.g. "escalation:{case}:{level}"); the same key is never notified twice to one recipient.</summary>
    public string? EventKey { get; set; }
}
