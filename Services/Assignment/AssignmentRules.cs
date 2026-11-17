namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// THE answer to "how would a new case of this department be given out?", for every way a case is made — Create Case and an
/// email ticket's conversion alike: the department's team, the algorithm for the case's source (email cases may have their own),
/// and whether a person must choose the agent (Manual). One implementation, so the two screens and the server never disagree.
/// </summary>
public static class AssignmentRules
{
    /// <summary>The source a case's source channel is given out as.</summary>
    public static string SourceOfChannel(string? sourceChannel) =>
        string.Equals(sourceChannel?.Trim(), "Email", StringComparison.OrdinalIgnoreCase) ? AssignmentCaseSources.Email : AssignmentCaseSources.Default;

    /// <summary>The rule for a department's new cases, or null when the department has no active team (the case is refused anyway).</summary>
    public static async Task<AssignmentRuleDto?> ForDepartmentAsync(AppDbContext db, IConfigCache? cache, Guid departmentId, string? sourceChannel, CancellationToken ct = default)
    {
        var team = await DepartmentTeams.TeamOfAsync(db, departmentId, ct);
        if (team is not { IsActive: true }) return null;
        var source = SourceOfChannel(sourceChannel);
        var config = await AssignmentSettings.ResolveForSourceAsync(db, cache, team.Id, source, ct);
        return new AssignmentRuleDto(team.Id, team.Name, config.Algorithm, source, config.Algorithm == AssignmentAlgorithms.Manual);
    }

    /// <summary>The refusal when the rule needs a person to choose the agent and none was chosen.</summary>
    public static FieldValidationException AgentRequired(AssignmentRuleDto rule, string field) =>
        new([new FieldError(field, rule.CaseSource == AssignmentCaseSources.Email
            ? "Email cases are assigned by hand: choose the agent who takes this case."
            : "Cases are assigned by hand: choose the agent who takes this case.")]);

    /// <summary>
    /// Who in a department's team could take a new case, as the Assign &amp; Reassign rules judge them, with the reason anyone cannot.
    /// Empty when the department has no active team.
    /// </summary>
    public static async Task<List<AssigneeOptionDto>> AssigneesAsync(AppDbContext db, IAssignmentEligibilityService eligibility, Guid departmentId, CancellationToken ct = default)
    {
        _ = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId && d.IsActive, ct)
            ?? throw new KeyNotFoundException("Department not found.");
        var team = await DepartmentTeams.TeamOfAsync(db, departmentId, ct);
        if (team is not { IsActive: true }) return new();
        var policy = await eligibility.GetManualPolicyAsync(ct);
        var result = await eligibility.EvaluateForCaseAsync(team.Id, policy, ct);
        return result.Candidates
            .OrderByDescending(c => c.IsEligible).ThenBy(c => c.IsCrossTeam).ThenBy(c => c.User.Name)
            .Select(c => new AssigneeOptionDto(c.User.Id, c.User.Name, c.MemberRole, c.HomeTeamName, c.IsTeamLead, c.IsCrossTeam,
                c.IsEligible, c.IsEligible ? null : c.Explain(result.Team.Name), c.User.Status.ToString(), c.OpenCases, c.Capacity))
            .ToList();
    }
}
