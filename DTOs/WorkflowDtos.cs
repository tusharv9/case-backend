namespace CaseManagement.Api.DTOs;

/// <summary>What the caller may do with a case's status right now — the server's own rules, for the screen to follow.</summary>
public class CaseWorkflowDto
{
    /// <summary>Statuses the status menu may offer ("InProgress", "WaitingOnCustomer").</summary>
    public List<string> AllowedStatuses { get; set; } = new();
    public bool CanResolve { get; set; }
    public bool CanEscalate { get; set; }
    public bool CanReopen { get; set; }
    /// <summary>The case was escalated to someone else: the caller may look but not change it.</summary>
    public bool IsLocked { get; set; }
    public string? LockedReason { get; set; }
    /// <summary>The caller may supervise cases (bulk operations, acting on escalated cases).</summary>
    public bool CanSupervise { get; set; }
}

/// <summary>The levels a case can be escalated to by hand, each with the people who may receive it.</summary>
public class EscalationOptionsDto
{
    public Guid CaseId { get; set; }
    public int CurrentLevel { get; set; }
    public string CurrentLevelName { get; set; } = "Not escalated";
    public bool CanEscalate { get; set; }
    /// <summary>Why the case cannot be escalated now (status, lock, no level above).</summary>
    public string? BlockedReason { get; set; }
    public List<EscalationLevelOptionDto> Levels { get; set; } = new();
}

public class EscalationLevelOptionDto
{
    public int LevelNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<EscalationRecipientDto> Recipients { get; set; } = new();
    /// <summary>Set when the people holding the role could not be read from OmniConnect.</summary>
    public string? RecipientsUnavailableMessage { get; set; }
}

public class EscalationRecipientDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    /// <summary>CaseTeam (of the team handling the case), Department (of the team the case's department is assigned to) or Role (anyone in the role).</summary>
    public string Scope { get; set; } = string.Empty;
}

// ------------------------------------------------------------------------------------------------ collaboration & swarm

public class TeamOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsCaseTeam { get; set; }
}

public class CollaboratorCandidateDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TeamRole { get; set; } = string.Empty;
    public bool IsTeamLead { get; set; }
    /// <summary>Already on the case (collaborator, holder): shown but not selectable.</summary>
    public bool IsUnavailable { get; set; }
    public string? UnavailableReason { get; set; }
}

public class AddCollaboratorsDto
{
    /// <summary>The team the collaborators were chosen from; each one must belong to it.</summary>
    public Guid? TeamId { get; set; }
    public List<Guid> CoworkerIds { get; set; } = new();
}

public class SwarmRecipientDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    /// <summary>TeamLead or Supervisor.</summary>
    public string Kind { get; set; } = string.Empty;
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public bool IsCaseTeam { get; set; }
    public bool HasPendingRequest { get; set; }
}

public class SwarmCandidatesDto
{
    public Guid? CaseTeamId { get; set; }
    public string? CaseTeamName { get; set; }
    /// <summary>Every active team with a lead, the case's own team first.</summary>
    public List<SwarmRecipientDto> TeamLeads { get; set; } = new();
    public List<SwarmRecipientDto> Supervisors { get; set; } = new();
    public string? SupervisorsUnavailableMessage { get; set; }
}

public class CreateSwarmRequestDto
{
    public Guid RecipientUserId { get; set; }
    /// <summary>TeamLead or Supervisor.</summary>
    public string RecipientKind { get; set; } = string.Empty;
    /// <summary>For a Team Lead: the team they were chosen for.</summary>
    public Guid? TeamId { get; set; }
    public string? Message { get; set; }
}

public class RespondSwarmRequestDto
{
    public bool Accept { get; set; }
    public string? Note { get; set; }
}

public class SwarmRequestDto
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public Guid RecipientUserId { get; set; }
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientKind { get; set; } = string.Empty;
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ResponseNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
}

// ------------------------------------------------------------------------------------------------ bulk operations

public class BulkCaseQueryDto
{
    /// <summary>reassign or resolve.</summary>
    public string Operation { get; set; } = "reassign";
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? TeamId { get; set; }
    public string? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class BulkCaseRowDto
{
    public Guid Id { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public int EscalationLevel { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BulkAssigneeDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    /// <summary>Team Lead or the member's role on the team.</summary>
    public string Role { get; set; } = string.Empty;
    public bool IsTeamLead { get; set; }
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public int OpenCases { get; set; }
}

public class BulkReassignRequestDto
{
    public List<Guid> CaseIds { get; set; } = new();
    public Guid DepartmentId { get; set; }
    public Guid AssigneeUserId { get; set; }
    /// <summary>The team the assignee was chosen in: the team the department is assigned to.</summary>
    public Guid? TeamId { get; set; }
    public string? Reason { get; set; }
    public string? ReasonDetail { get; set; }
}

public class BulkResolveRequestDto
{
    public List<Guid> CaseIds { get; set; } = new();
    public string Disposition { get; set; } = string.Empty;
    public string ResolutionNote { get; set; } = string.Empty;
}

public class BulkResultItemDto
{
    public Guid CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
}

public class BulkResultDto
{
    public int Requested { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public List<BulkResultItemDto> Items { get; set; } = new();
}
