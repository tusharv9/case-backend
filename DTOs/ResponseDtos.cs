using CaseManagement.Api.Services;
namespace CaseManagement.Api.DTOs;

public class CaseSummaryDto
{
    public Guid Id { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public DateTime SlaStartTime { get; set; }
    public int SlaTargetHours { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? SlaPausedAt { get; set; }
    public int SlaTotalPausedMinutes { get; set; }
    public bool IsHolidayToday { get; set; }
    public string? HolidayName { get; set; }
    public bool IsBusinessHoursActive { get; set; } = true;

    // Resolution targets as snapshotted when the case was created (business minutes). The SLA fields above and below are the
    // raw inputs; <see cref="Sla"/> is the server's verdict on them and is what the UI shows.
    public int InternalResolutionTargetMinutes { get; set; }
    public int ExternalResolutionTargetMinutes { get; set; }

    /// <summary>How the case stands against its SLA right now, computed by the one SLA clock. Null only for cases not yet evaluated.</summary>
    public CaseSlaDto? Sla { get; set; }
    public bool IsSlaPaused => Sla?.IsPaused ?? false;
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    /// <summary>The business department the case belongs to (DepartmentId/Name above are its TEAM). Null for cases from before departments.</summary>
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string? OwnerRole { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>The local customer (New Customer); null for a Customer 360 customer.</summary>
    public Guid? CustomerId { get; set; }
    /// <summary>
    /// The local customer's name. Empty for a Customer 360 customer: their name is not stored, and is shown on the
    /// case itself, read live from Customer 360 (GET api/cases/{id}/customer360).
    /// </summary>
    public string CustomerName { get; set; } = string.Empty;
    /// <summary>Local or Customer360.</summary>
    public string CustomerSource { get; set; } = "Local";
    /// <summary>Customer 360 only: Individual or NonIndividual.</summary>
    public string? CustomerType { get; set; }
    /// <summary>Customer 360 only: Customer 360's own key for the customer — names them when listing their cases. Not shown.</summary>
    public string? ExternalCustomerId { get; set; }

    public string CaseType { get; set; } = "Complaint";
    public string? Subcategory { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? SourceChannel { get; set; }
    public string? PreferredCommunicationChannel { get; set; }
    public string? CommunicationChannel { get; set; }
    public Guid? ParentCaseId { get; set; }
    public string? ParentCaseNumber { get; set; }
    public string SubcaseType { get; set; } = "Original";
    public bool IsSubcase => ParentCaseId.HasValue || (SubcaseType != null && SubcaseType != "Original");
    public List<CaseChildRelationDto> ChildRelations { get; set; } = new();

    // First Response SLA Tracking
    public int FirstResponseTargetMinutes { get; set; }
    public DateTime? FirstResponseDueAt { get; set; }
    public DateTime? FirstResponseActualAt { get; set; }
    public string FirstResponseStatus { get; set; } = "Pending"; // Met, Breached, Pending

    // Escalation Matrix Tracking
    /// <summary>0 = not escalated; n = the escalation matrix's Level n.</summary>
    public int EscalationLevel { get; set; }
    public string EscalationLevelName => EscalationLevel > 0 ? $"Level {EscalationLevel}" : "Not escalated";
}

public class CaseDetailDto : CaseSummaryDto
{
    /// <summary>Who opened the case (the user on its Create event) — never inferred from the owner, who may be someone else.</summary>
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public List<CustomerCustomAttributeDto> CustomAttributes { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public string? Disposition { get; set; }
    public string? ResolutionNote { get; set; }
    public CustomerSummaryDto Customer { get; set; } = null!;
    public List<CaseEventDto> Events { get; set; } = new();
    public List<ParticipantDto> Participants { get; set; } = new();
    public List<LinkedCaseDto> LinkedCases { get; set; } = new();
    public List<CaseSummaryDto> Subcases { get; set; } = new();
    public List<CaseAttachmentDto> Attachments { get; set; } = new();
    /// <summary>What the caller may do with the case's status now; null when there is no signed-in caller.</summary>
    public CaseWorkflowDto? Workflow { get; set; }
}

public class CaseChildRelationDto
{
    public string ChildId { get; set; } = string.Empty; // e.g. C-00045-L01, C-00045-R01
    public string RelationType { get; set; } = string.Empty; // Link, Reopen
    public string? LinkedCaseNumber { get; set; }
    public string? LinkedCaseTitle { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class LinkCaseResultDto
{
    public string Message { get; set; } = string.Empty;
    public string ChildId { get; set; } = string.Empty; // e.g. C-00045-L01
    public Guid? NewCaseId { get; set; }
    public string LinkedCaseNumber { get; set; } = string.Empty;
}

public class ReopenCaseResultDto
{
    public string Message { get; set; } = string.Empty;
    public string ChildId { get; set; } = string.Empty; // e.g. C-00045-R01
    public Guid? NewCaseId { get; set; }
}

public class CustomerSummaryDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? NRIC { get; set; }
    public string? Passport { get; set; }
    public string? AccountNumber { get; set; }
    public string IdType { get; set; } = "NRIC Number";
    public string IdValue { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Branch { get; set; }
    public string? PreferredLanguage { get; set; }
    public int OpenCasesCount { get; set; }
    public int TotalCasesCount { get; set; }
    public List<CustomerCustomAttributeDto> CustomAttributes { get; set; } = new();
}

public class CustomerDetailDto : CustomerSummaryDto
{
    public string? CustomerSegment { get; set; }
    public List<CaseSummaryDto> Cases { get; set; } = new();
}

public class CaseEventDto
{
    public Guid Id { get; set; }
    /// <summary>Null when the person who did it has since been removed.</summary>
    public Guid? UserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string UserName { get; set; } = string.Empty;
    public bool IsInternal { get; set; } = true;
    public string? Channel { get; set; }
}

public class ParticipantDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class LinkedCaseDto
{
    public Guid TargetCaseId { get; set; }
    public string TargetCaseNumber { get; set; } = string.Empty;
    public string TargetCaseTitle { get; set; } = string.Empty;
}

public class UserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    /// <summary>The teams the person is an active member of (comma-separated), from team membership.</summary>
    public string? Team { get; set; }
    public string? ExternalUserId { get; set; }
    public bool IsActive { get; set; } = true;
}


public class CaseTypeConfigDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = "C-";
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public class DepartmentSubCategoryDto
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public class EscalationMatrixLevelDto
{
    public int Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? CurrentTargetUserName { get; set; }
    public Guid? CurrentTargetUserId { get; set; }
}

public class EscalationMatrixResponseDto
{
    public string Title { get; set; } = "Escalation matrix";
    public string Subtitle { get; set; } = "manual escalation — cases are never escalated automatically; every escalation is audit-logged";
    public int CurrentLevel { get; set; } = 1;
    public int? NextLevel { get; set; }
    public string? NextTargetRole { get; set; }
    public string? NextTargetUserName { get; set; }
    public Guid? NextTargetUserId { get; set; }
    public List<EscalationMatrixLevelDto> Levels { get; set; } = new();
}

public class SlaTargetDto
{
    public int TargetMinutes { get; set; }
    public double ConsumedMinutes { get; set; }
    public double RemainingMinutes { get; set; }
    public double ConsumedPercent { get; set; }
    public DateTime? DueAt { get; set; }
    public bool IsBreached { get; set; }
}

public class CaseSlaDto
{
    /// <summary>Healthy | Approaching | Breached | Paused | Met</summary>
    public string Health { get; set; } = "Healthy";
    public bool IsPaused { get; set; }
    public bool IsStopped { get; set; }

    /// <summary>True while the clock is actually counting (not paused, not resolved, and inside working hours right now).</summary>
    public bool IsClockRunning { get; set; }
    public SlaTargetDto Internal { get; set; } = new();
    public SlaTargetDto External { get; set; } = new();
    public SlaTargetDto FirstResponse { get; set; } = new();
    public string FirstResponseStatus { get; set; } = "Pending";
    public DateTime ComputedAt { get; set; }

    public static CaseSlaDto From(SlaSnapshot s) => new()
    {
        Health = s.Health.ToString(),
        IsPaused = s.IsPaused,
        IsStopped = s.IsStopped,
        IsClockRunning = s.IsClockRunning,
        Internal = Target(s.Internal),
        External = Target(s.External),
        FirstResponse = Target(s.FirstResponse),
        FirstResponseStatus = s.FirstResponseStatus,
        ComputedAt = s.ComputedAt,
    };

    private static SlaTargetDto Target(CaseManagement.Api.Services.SlaTargetState t) => new()
    {
        TargetMinutes = t.TargetMinutes,
        ConsumedMinutes = t.ConsumedMinutes,
        RemainingMinutes = t.RemainingMinutes,
        ConsumedPercent = t.ConsumedPercent,
        DueAt = t.DueAt,
        IsBreached = t.IsBreached,
    };
}
