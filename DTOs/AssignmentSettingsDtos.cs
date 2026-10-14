namespace CaseManagement.Api.DTOs;

/// <summary>Configurable Settings → Case Management → Assign &amp; Reassign: everything the tab shows, from the database.</summary>
public class AssignmentSettingsDto
{
    // Assignment scope / cross-team
    public string CrossTeamMode { get; set; } = string.Empty;          // Disabled | SelectedTeams | AllTeams
    public List<Guid> CrossTeamDepartmentIds { get; set; } = new();
    public List<string> CrossTeamModes { get; set; } = new();

    // Eligible roles, availability and workload
    public bool IncludeTeamLeads { get; set; }
    public bool RequireAvailable { get; set; }
    public bool EnforceCapacity { get; set; }

    // Default routing strategy (the global automatic-assignment settings; teams may override them on the Teams page)
    public string DefaultAlgorithm { get; set; } = string.Empty;
    public int DefaultCapacity { get; set; }
    public List<string> Algorithms { get; set; } = new();

    public List<AssignmentSettingsTeamDto> Teams { get; set; } = new();

    /// <summary>When these settings were last saved; send it back on save so a newer save is never overwritten.</summary>
    public DateTime Version { get; set; }
}

public class AssignmentSettingsTeamDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public Guid? LeadId { get; set; }
    public string? LeadName { get; set; }
    /// <summary>The open-case capacity in force for this team (its own routing setting, else the global one).</summary>
    public int Capacity { get; set; }
    public bool HasOwnRoutingSettings { get; set; }
    public List<AssignmentSettingsMemberDto> Members { get; set; } = new();
}

public class AssignmentSettingsMemberDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsUserActive { get; set; }
    public bool IsLead { get; set; }
    /// <summary>Receives cases from AUTOMATIC routing (edited on the Teams page).</summary>
    public bool IsAssignable { get; set; }
    /// <summary>May be CHOSEN when assigning or reassigning (edited here).</summary>
    public bool CanBeManuallyAssigned { get; set; }
}

public class UpdateAssignmentSettingsDto
{
    public string CrossTeamMode { get; set; } = string.Empty;
    public List<Guid> CrossTeamDepartmentIds { get; set; } = new();
    public bool IncludeTeamLeads { get; set; }
    public bool RequireAvailable { get; set; }
    public bool EnforceCapacity { get; set; }
    public string DefaultAlgorithm { get; set; } = string.Empty;
    public int DefaultCapacity { get; set; }
    /// <summary>Member selection changes (only the rows sent are changed).</summary>
    public List<MemberSelectionDto> MemberSelections { get; set; } = new();
    /// <summary>The Version the editor loaded. A different stored version means someone saved in between: 409, nothing is changed.</summary>
    public DateTime? ExpectedVersion { get; set; }
}

public class MemberSelectionDto
{
    public Guid DepartmentId { get; set; }
    public Guid UserId { get; set; }
    public bool CanBeManuallyAssigned { get; set; }
}
