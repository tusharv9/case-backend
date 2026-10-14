namespace CaseManagement.Api.DTOs;

public class CreateCustomerDto
{
    public string FullName { get; set; } = string.Empty;
    public string IdType { get; set; } = string.Empty;
    public string? IdValue { get; set; }
    public string? NRIC { get; set; }
    public string? Passport { get; set; }
    public string? AccountNumber { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Country whose rules the phone number is checked against (ISO 3166-1 alpha-2). Blank = the default country.</summary>
    public string? PhoneCountryIso2 { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string? CustomerSegment { get; set; }
    public string PreferredLanguage { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public Dictionary<string, string>? CustomAttributes { get; set; }
}

public class CreateCaseDto
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Severity { get; set; } = string.Empty;

    /// <summary>Ignored: SLA targets come from the priority's configured rule, never from the client.</summary>
    public int SlaTargetHours { get; set; }

    public string CaseType { get; set; } = string.Empty;
    public string? Subcategory { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? SourceChannel { get; set; }
    public string? PreferredCommunicationChannel { get; set; }
    public string? CommunicationChannel { get; set; }

    /// <summary>Values of administrator-defined custom fields on the Create Case form, keyed by field key.</summary>
    public Dictionary<string, string>? CustomAttributes { get; set; }
}

public class UpdateCaseStatusDto
{
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
    public Guid UserId { get; set; } // The user making the change
}

public class EscalateCaseDto
{
    /// <summary>An active ESCALATION_REASON option (its value or label).</summary>
    public string? Reason { get; set; }
    /// <summary>The explanation, required when the chosen option RequiresDetail (e.g. "Other").</summary>
    public string? ReasonDetail { get; set; }
    public string? Note { get; set; }
    public Guid? TargetUserId { get; set; }
    public Guid UserId { get; set; }
}

public class AssignCaseDto
{
    public Guid OwnerId { get; set; }
    public Guid UserId { get; set; } // The user making the change
    /// <summary>An active ASSIGNMENT_REASON option (its value or label).</summary>
    public string? Reason { get; set; }
    /// <summary>The explanation, required when the chosen option RequiresDetail (e.g. "Others").</summary>
    public string? ReasonDetail { get; set; }
}

/// <summary>Who could hold a case under the manual-assignment policy. Every figure is read from the database; nothing is inferred.</summary>
public class EligibleAssigneesDto
{
    public Guid CaseId { get; set; }
    public Guid DepartmentId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public bool DepartmentIsActive { get; set; }
    public Guid? CurrentOwnerId { get; set; }
    /// <summary>Open cases an agent may hold in this team (the team's own setting, else the global one).</summary>
    public int Capacity { get; set; }
    /// <summary>The Assign &amp; Reassign cross-team setting in force (Disabled | SelectedTeams | AllTeams).</summary>
    public string CrossTeamMode { get; set; } = string.Empty;
    public List<EligibleAssigneeDto> Candidates { get; set; } = new();
}

public class EligibleAssigneeDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    /// <summary>The person's role within this team (TeamMembers.MemberRole); null for a lead who has no member row.</summary>
    public string? TeamRole { get; set; }
    public bool IsTeamLead { get; set; }
    public bool IsTeamMember { get; set; }
    /// <summary>The team this person is offered from: the case's team, or another team when cross-team assignment is allowed.</summary>
    public string HomeTeamName { get; set; } = string.Empty;
    public bool IsCrossTeam { get; set; }
    /// <summary>Users.Status as the agent last set it (Available, Busy, Away, Offline).</summary>
    public string Status { get; set; } = string.Empty;
    public int OpenCases { get; set; }
    public bool IsCurrentOwner { get; set; }
    public bool IsEligible { get; set; }
    /// <summary>Machine-readable reason (IneligibilityReason name, or "CurrentOwner"); null when eligible.</summary>
    public string? IneligibleReason { get; set; }
    public string? IneligibleMessage { get; set; }
}

public class AddNoteDto
{
    public string Message { get; set; } = string.Empty;
    public Guid UserId { get; set; }
}

public class AddCoworkersDto
{
    public List<Guid> CoworkerIds { get; set; } = new();
}

public class TransferDepartmentDto
{
    public Guid DepartmentId { get; set; }
    public string? TransferTo { get; set; } // "Queue", "Department", "Team"
    public string? TargetQueue { get; set; }
    /// <summary>An active TRANSFER_REASON option (its value or label).</summary>
    public string? Reason { get; set; }
    /// <summary>The explanation, required when the chosen option RequiresDetail.</summary>
    public string? ReasonDetail { get; set; }
    public string? HandoverNote { get; set; }
}

public record RelatedCustomerCaseDto(
    Guid Id,
    string CaseNumber,
    string CaseType,
    string Title,
    string Status,
    string Severity,
    DateTime CreatedAt,
    bool IsAlreadyLinked
);


public class CreateUserDto
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public Guid DepartmentId { get; set; }
}

public class CreateDepartmentDto
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class LinkCaseDto
{
    public string RelationshipType { get; set; } = string.Empty;
    public string TargetCaseNumber { get; set; } = string.Empty;
    public Guid UserId { get; set; }
}

public class ResolveCaseDto
{
    public string Disposition { get; set; } = string.Empty;
    public string ResolutionNote { get; set; } = string.Empty;
    public Guid UserId { get; set; }
}

public class ReopenCaseDto
{
    public string Message { get; set; } = string.Empty;
    public Guid UserId { get; set; }
}

public class UnlinkCaseDto
{
    public string TargetCaseNumber { get; set; } = string.Empty;
    public Guid UserId { get; set; }
}

public class SetDepartmentOwnerDto
{
    public Guid OwnerId { get; set; }
}

public class UpdateUserStatusDto
{
    public string Status { get; set; } = string.Empty;
}

public class AddTimelineInteractionDto
{
    public string Message { get; set; } = string.Empty;
    public bool IsInternal { get; set; } = true;
    public string? Channel { get; set; }
    public Guid UserId { get; set; }
}

public class RequestSwarmDto
{
    public string? Reason { get; set; }
    public Guid UserId { get; set; }
    public List<Guid>? ParticipantUserIds { get; set; }
}

public class CaseAttachmentDto
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? Note { get; set; }
    public Guid UploadedByUserId { get; set; }
    public string UploadedByUserName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
