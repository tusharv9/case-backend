namespace CaseManagement.Api.DTOs;

/// <summary>
/// Case Settings → Teams → Team Rules: the team policies, from the database. Each one is enforced by the server on every team
/// save. (The Create/Edit Team form is configured as fields, through the field-configuration API, section "CreateTeam".)
/// </summary>
public class TeamSettingsDto
{
    // Membership
    public bool AllowMultipleTeamMembership { get; set; }
    public bool AllowTeamsWithoutMembers { get; set; }
    // Leadership
    public bool AllowMultipleTeamLeads { get; set; }
    public bool AllowLeadInOtherTeams { get; set; }
    public bool RequireTeamLead { get; set; }
    // Departments
    public bool AllowMultipleDepartments { get; set; }
    // Lifecycle
    public bool AllowDeactivateWithOpenCases { get; set; }
    /// <summary>When these settings were last saved; send it back on save so a newer save is never overwritten.</summary>
    public DateTime Version { get; set; }
}

/// <summary>Every policy, all required: a save states the whole set, so a client that does not know a newer policy cannot reset it by omission.</summary>
public class UpdateTeamSettingsDto
{
    public bool? AllowMultipleTeamMembership { get; set; }
    public bool? AllowTeamsWithoutMembers { get; set; }
    public bool? AllowMultipleTeamLeads { get; set; }
    public bool? AllowLeadInOtherTeams { get; set; }
    public bool? RequireTeamLead { get; set; }
    public bool? AllowMultipleDepartments { get; set; }
    public bool? AllowDeactivateWithOpenCases { get; set; }
    /// <summary>The Version the editor loaded. A different stored version means someone saved in between: 409, nothing is changed.</summary>
    public DateTime? ExpectedVersion { get; set; }
}

/// <summary>A business department, as the settings list shows it.</summary>
public class OrgDepartmentDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int DisplayOrder { get; set; }
    /// <summary>Teams that serve this department (active or not).</summary>
    public int TeamCount { get; set; }
    /// <summary>The teams that serve it, the default one marked: where its cases go when no routing rule matches.</summary>
    public List<DepartmentTeamDto> Teams { get; set; } = new();
    /// <summary>Cases that belong to this department.</summary>
    public int CaseCount { get; set; }
}

public class DepartmentTeamDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
}

public class SetDefaultTeamDto
{
    /// <summary>A team that serves the department, or null for no default.</summary>
    public Guid? TeamId { get; set; }
}

public class SaveOrgDepartmentDto
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
