namespace CaseManagement.Api.Models;

/// <summary>
/// Case Settings → Teams → Team Rules. One row of team policies, enforced by <c>TeamService</c> on every team save whatever the
/// screen. Every policy starts at the behaviour from before it existed, so existing teams stay valid; tightening one is
/// refused while existing teams break it (the refusal names them). The Create/Edit Team form itself is configured as
/// <c>FieldConfiguration</c> rows (module CaseManagement, section <see cref="TeamFormFields.SectionKey"/>).
/// </summary>
public class TeamSettings : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>A person may be a member of several teams. "Member" excludes a team's own lead (see AllowMultipleTeamLeads).</summary>
    public bool AllowMultipleTeamMembership { get; set; } = true;

    /// <summary>A person may lead several teams.</summary>
    public bool AllowMultipleTeamLeads { get; set; } = true;

    /// <summary>A team may serve several business departments; off = at most one.</summary>
    public bool AllowMultipleDepartments { get; set; } = true;

    /// <summary>A team may be saved with no members (it then holds no one to route to; cases wait for its lead).</summary>
    public bool AllowTeamsWithoutMembers { get; set; } = true;

    /// <summary>The lead of one team may also be a member of other teams.</summary>
    public bool AllowLeadInOtherTeams { get; set; } = true;

    /// <summary>Every active team must have a lead.</summary>
    public bool RequireTeamLead { get; set; }

    /// <summary>A team that still holds open cases may be deactivated (they stay with it until moved).</summary>
    public bool AllowDeactivateWithOpenCases { get; set; } = true;

    /// <summary>
    /// Superseded: the first version of the configurable team form (show/require per field), stored as jsonb. The migration
    /// copied it into FieldConfiguration rows; nothing reads it any more. Kept so a rollback finds it unchanged.
    /// </summary>
    public List<TeamFormFieldRule> FormFields { get; set; } = TeamFormFields.Defaults();
}

/// <summary>How one field of the Create/Edit Team form behaves. A hidden field is never required, and hiding it keeps saved values.</summary>
public class TeamFormFieldRule
{
    public string Key { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
    public bool IsRequired { get; set; }
}

public static class TeamFormFields
{
    /// <summary>The FieldConfiguration section the Create/Edit Team form is configured in.</summary>
    public const string SectionKey = "CreateTeam";
    public const string Name = "name";
    public const string Code = "code";
    public const string Description = "description";
    public const string Departments = "departments";
    public const string TeamLead = "teamLead";
    public const string Members = "members";

    /// <summary>Every configurable field, in form order, with its label.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All = new[]
    {
        (Description, "Description"),
        (Departments, "Departments"),
        (TeamLead, "Team lead"),
        (Members, "Members"),
    };

    /// <summary>Everything shown, nothing required: exactly what the form accepted before it was configurable.</summary>
    public static List<TeamFormFieldRule> Defaults() =>
        All.Select(f => new TeamFormFieldRule { Key = f.Key, IsVisible = true, IsRequired = false }).ToList();

    /// <summary>The stored rules completed with defaults for any field added since they were saved, unknown keys dropped.</summary>
    public static List<TeamFormFieldRule> Normalise(IEnumerable<TeamFormFieldRule>? stored)
    {
        var byKey = (stored ?? Enumerable.Empty<TeamFormFieldRule>())
            .Where(r => r != null && All.Any(f => f.Key == r.Key))
            .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last());
        return All.Select(f => byKey.TryGetValue(f.Key, out var r)
                ? new TeamFormFieldRule { Key = f.Key, IsVisible = r.IsVisible, IsRequired = r.IsVisible && r.IsRequired }
                : new TeamFormFieldRule { Key = f.Key, IsVisible = true, IsRequired = false })
            .ToList();
    }
}
