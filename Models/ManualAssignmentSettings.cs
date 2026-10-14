namespace CaseManagement.Api.Models;

/// <summary>
/// How a PERSON may choose who holds a case (Assign / Reassign). One row: the policy is the same for every team. Automatic
/// routing has its own settings (<see cref="AssignmentConfiguration"/>). Edited under Configurable Settings → Case Management →
/// Assign &amp; Reassign; enforced by AssignmentEligibilityService on every assignment, whatever the screen.
/// </summary>
public class ManualAssignmentSettings : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The team's lead (Department.OwnerId) may be chosen even without a member row.</summary>
    public bool IncludeTeamLeads { get; set; } = true;

    /// <summary>Only people whose status is Available or Busy can be chosen (Away / Offline are refused).</summary>
    public bool RequireAvailable { get; set; } = true;

    /// <summary>Only people below the case team's open-case capacity can be chosen.</summary>
    public bool EnforceCapacity { get; set; } = true;

    /// <summary><see cref="CrossTeamModes"/>: who outside the case's own team may be chosen.</summary>
    public string CrossTeamMode { get; set; } = CrossTeamModes.Disabled;

    public ICollection<ManualAssignmentCrossTeam> CrossTeams { get; set; } = new List<ManualAssignmentCrossTeam>();
}

public static class CrossTeamModes
{
    /// <summary>Only the case's own team.</summary>
    public const string Disabled = "Disabled";
    /// <summary>The case's own team plus the members of the teams listed in ManualAssignmentCrossTeams.</summary>
    public const string SelectedTeams = "SelectedTeams";
    /// <summary>The case's own team plus the members of every active team.</summary>
    public const string AllTeams = "AllTeams";

    public static readonly string[] All = { Disabled, SelectedTeams, AllTeams };
}

/// <summary>A team whose members may be given cases of other teams when the cross-team mode is SelectedTeams.</summary>
public class ManualAssignmentCrossTeam
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SettingsId { get; set; }
    public ManualAssignmentSettings Settings { get; set; } = null!;
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
}
