namespace CaseManagement.Api.DTOs;

public class NotificationDto
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Guid? CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public bool IsRead { get; set; }
    public string Priority { get; set; } = "High";
    public int ReminderCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class UnreadCountDto
{
    public int UnreadCount { get; set; }
}
