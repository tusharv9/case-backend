namespace CaseManagement.Api.Models;

public class CaseEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CaseId { get; set; }
    public Case? Case { get; set; }
    
    public EventType EventType { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    
    /// <summary>Who did it. Null only when that person's record has since been removed (shown as "Former user").</summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    // Configurable Settings & Audit Trail enhancements
    public string? Module { get; set; }
    public string? EntityName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? ActionType { get; set; }

    // Case Details Drawer Timeline Interaction & Mentions
    public bool IsInternal { get; set; } = true;
    public string? Channel { get; set; }
}
