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
    /// <summary>Not recorded by the audit trail (kept for older screens); null rather than a made-up "Success".</summary>
    public string? Status { get; set; }
    /// <summary>Not recorded by the audit trail; null rather than a placeholder address.</summary>
    public string? IpAddress { get; set; }
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

/// <summary>What the audit trail is filtered by. Every filter narrows; none can widen past what the caller may see.</summary>
public sealed record AuditFilter(
    string? ActionType = null,
    string? Search = null,
    Guid? ActorId = null,
    Guid? TeamId = null,
    Guid? DepartmentId = null,
    DateTime? From = null,
    DateTime? To = null,
    string? CaseNumber = null,
    /// <summary>Any of these people (Team Monitoring's export of a team's activity). Combined with <see cref="ActorId"/> when both are set.</summary>
    IReadOnlyCollection<Guid>? ActorIds = null);

/// <summary>The audit trail's filter choices, limited to what the caller may see.</summary>
public class AuditFiltersDto
{
    public bool SeesAllTeams { get; set; }
    public List<NamedOptionDto> Actors { get; set; } = new();
    public List<NamedOptionDto> Teams { get; set; } = new();
    public List<NamedOptionDto> Departments { get; set; } = new();
    public List<FilterOptionDto> ActionTypes { get; set; } = new();
    /// <summary>The most rows one export holds.</summary>
    public int MaxExportRows { get; set; }
}

/// <summary>The one place an audit event's display label is decided (the trail, its export and the agent drawer).</summary>
public static class AuditLabels
{
    /// <summary>The action types the trail can be filtered by, as (value, label) — the server's filter understands each value.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Filters = new[]
    {
        ("create", "Case created"),
        ("assign", "Case assigned"),
        ("status", "Case status changed"),
        ("note", "Note added"),
        ("comment", "Customer reply"),
        ("escalate", "Case escalated"),
        ("transfer", "Team transferred"),
        ("cowork", "Co-worker added"),
        ("resolve", "Case resolved"),
        ("close", "Case closed"),
        ("view", "Case viewed"),
        ("nudge", "Agent nudged"),
        ("team", "Teams and departments"),
        ("config", "Configuration changes"),
    };

    public static string For(CaseManagement.Api.Models.EventType eventType, string? actionType, string? module)
    {
        if (string.Equals(actionType, "NUDGE", StringComparison.OrdinalIgnoreCase)) return "Agent nudged";
        if (!string.IsNullOrEmpty(actionType))
        {
            var verb = actionType.Replace('_', ' ').ToLowerInvariant();
            verb = char.ToUpperInvariant(verb[0]) + verb[1..];
            return string.IsNullOrWhiteSpace(module) ? verb : $"{module}: {verb}";
        }
        return eventType switch
        {
            CaseManagement.Api.Models.EventType.Create => "Case Created",
            CaseManagement.Api.Models.EventType.Assign => "Case Assigned",
            CaseManagement.Api.Models.EventType.Note => "Note Added",
            CaseManagement.Api.Models.EventType.Comment => "Customer Reply",
            CaseManagement.Api.Models.EventType.Cowork => "Coworker Added",
            CaseManagement.Api.Models.EventType.Transfer => "Team Transferred",
            CaseManagement.Api.Models.EventType.Escalate => "Case Escalated",
            CaseManagement.Api.Models.EventType.Resolve => "Case Resolved",
            CaseManagement.Api.Models.EventType.Close => "Case Closed",
            CaseManagement.Api.Models.EventType.View => "Case Viewed",
            _ => "Case Event"
        };
    }
}
