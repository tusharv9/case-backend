namespace CaseManagement.Api.DTOs;

public class CaseAuditEventDto
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string ActionLabel { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public Guid? CaseId { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string CaseTitle { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "Success";
    public string IpAddress { get; set; } = "127.0.0.1";
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? PreviousOwner { get; set; }
    public string? NewOwner { get; set; }
    public string? DepartmentName { get; set; }
    public string? Severity { get; set; }
    public string? Module { get; set; }
    public string? EntityName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
