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

    public List<TeamMemberDto> Members { get; set; } = new();
}

public class TeamMemberDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
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
