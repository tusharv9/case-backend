namespace CaseManagement.Api.DTOs;

public class TeamDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;

    public Guid? TeamLeadId { get; set; }
    public string TeamLeadName { get; set; } = string.Empty;
    public string TeamLeadEmail { get; set; } = string.Empty;

    public int MemberCount { get; set; }

    /// <summary>Open (unresolved) cases currently in this team.</summary>
    public int QueueCount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    /// <summary>How new cases are given to this team's agents, and how many each agent may hold at once.</summary>
    public string AssignmentAlgorithm { get; set; } = string.Empty;
    public int MaxConcurrentCapacity { get; set; }

    /// <summary>True when the team has its own settings; false when it follows the global default.</summary>
    public bool HasOwnAssignmentSettings { get; set; }

    /// <summary>The departments this team handles (each belongs to this team only).</summary>
    public List<TeamDepartmentDto> Departments { get; set; } = new();

    /// <summary>Values of the administrator-defined Create/Edit Team fields, by field key.</summary>
    public Dictionary<string, string> CustomAttributes { get; set; } = new();

    public List<TeamMemberDto> Members { get; set; } = new();
}

public class TeamDepartmentDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public class TeamMemberDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>The Host's id for the person; null for a person who exists only in Case Management (pre-Host sample data).</summary>
    public string? ExternalUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = "Available";
    public bool IsLead { get; set; }

    /// <summary>Whether cases are routed to this person automatically.</summary>
    public bool IsAssignable { get; set; } = true;
}

public class TeamMemberInputDto
{
    public Guid UserId { get; set; }
    public bool IsAssignable { get; set; } = true;
}

public class CreateTeamDto
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string Function { get; set; } = string.Empty;
    public Guid? TeamLeadId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>The members (and whether each receives cases). <see cref="MemberUserIds"/> is the shorthand for "all assignable".</summary>
    public List<TeamMemberInputDto>? Members { get; set; }
    public List<Guid>? MemberUserIds { get; set; }

    /// <summary>The departments the team handles; omit (null) for none. A department already assigned to another team is refused.</summary>
    public List<Guid>? DepartmentIds { get; set; }

    /// <summary>Values of the administrator-defined form fields (Case Settings → Teams → Create Team Form), by field key.</summary>
    public Dictionary<string, string>? CustomAttributes { get; set; }

    /// <summary>Optional team-specific assignment settings; omit to follow the global default.</summary>
    public string? AssignmentAlgorithm { get; set; }
    public int? MaxConcurrentCapacity { get; set; }
}

public class UpdateTeamDto
{
    public string? Name { get; set; }
    public string? Code { get; set; }
    public string? Function { get; set; }
    public Guid? TeamLeadId { get; set; }
    public bool? IsActive { get; set; }
    public List<TeamMemberInputDto>? Members { get; set; }
    public List<Guid>? MemberUserIds { get; set; }

    /// <summary>The departments the team handles. Null leaves them as they are (a hidden field never clears them). A department assigned to another team is refused.</summary>
    public List<Guid>? DepartmentIds { get; set; }

    /// <summary>Custom field values to change, by key; a blank value clears one. Null (or a key left out) keeps what is stored.</summary>
    public Dictionary<string, string>? CustomAttributes { get; set; }

    public string? AssignmentAlgorithm { get; set; }
    public int? MaxConcurrentCapacity { get; set; }

    /// <summary>Drop the team's own assignment settings so it follows the global default again.</summary>
    public bool UseGlobalAssignmentSettings { get; set; }
}

public class AddTeamMemberDto
{
    public Guid UserId { get; set; }
    public string? MemberRole { get; set; }
    public bool IsAssignable { get; set; } = true;
}

/// <summary>Why a person is taken off a team (or off its lead): required, kept in the team's audit history.</summary>
public class TeamRemovalDto
{
    public string? Reason { get; set; }
}

/// <summary>Activating a team: the departments it is to handle from now on (each must be active and free).</summary>
public class ToggleTeamDto
{
    public List<Guid>? DepartmentIds { get; set; }
}
