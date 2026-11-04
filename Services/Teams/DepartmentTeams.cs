using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaseManagement.Api.Services.Teams;

/// <summary>
/// The team–department mapping rule, in one place: a team handles any number of departments, a department is handled by at
/// most one team (unique index <see cref="UniqueIndexName"/>), and a department with no team cannot be used for a new case.
/// There is no default or fallback team: a case of a department goes to the team the department is mapped to, or nowhere.
/// </summary>
public static class DepartmentTeams
{
    /// <summary>The database index that makes a department belong to at most one team.</summary>
    public const string UniqueIndexName = "UX_TeamDepartments_Department";

    public sealed record MappedTeam(Guid Id, string Name, bool IsActive);

    /// <summary>The team the department is mapped to (active or not), or null when it has none.</summary>
    public static Task<MappedTeam?> TeamOfAsync(AppDbContext db, Guid departmentId, CancellationToken ct = default) =>
        db.TeamDepartments.AsNoTracking().Where(l => l.DepartmentId == departmentId)
            .Select(l => new MappedTeam(l.TeamId, l.Team.Name, l.Team.IsActive))
            .FirstOrDefaultAsync(ct);

    /// <summary>Whether the team is the one the department is mapped to. A case with no department (from before departments) has no constraint.</summary>
    public static Task<bool> HandlesAsync(AppDbContext db, Guid teamId, Guid? departmentId, CancellationToken ct = default) =>
        departmentId is not Guid dept
            ? Task.FromResult(true)
            : db.TeamDepartments.AsNoTracking().AnyAsync(l => l.TeamId == teamId && l.DepartmentId == dept, ct);

    /// <summary>
    /// Refuses any of <paramref name="departmentIds"/> that is already mapped to a team other than <paramref name="teamId"/>,
    /// naming the department and the team that holds it. A team's own departments are never a conflict, so editing a team
    /// keeps them without error. Never moves a department: the administrator removes it from the other team first.
    /// </summary>
    public static async Task EnsureNotMappedElsewhereAsync(AppDbContext db, IReadOnlyCollection<Guid> departmentIds, Guid teamId, string field, CancellationToken ct = default)
    {
        if (departmentIds.Count == 0) return;
        var taken = await db.TeamDepartments.AsNoTracking()
            .Where(l => departmentIds.Contains(l.DepartmentId) && l.TeamId != teamId)
            .OrderBy(l => l.Department.Name)
            .Select(l => new { Department = l.Department.Name, Team = l.Team.Name })
            .ToListAsync(ct);
        if (taken.Count == 0) return;
        var message = taken.Count == 1
            ? $"{taken[0].Department} is already assigned to the team '{taken[0].Team}'. A department can belong to only one team: remove it from '{taken[0].Team}' first."
            : $"These departments are already assigned to other teams: {string.Join("; ", taken.Select(t => $"{t.Department} → '{t.Team}'"))}. A department can belong to only one team: remove them from those teams first.";
        throw new FieldValidationException(new[] { new FieldError(field, message) });
    }

    /// <summary>The refusal for a department that no team handles (Create Case, routing, transfer).</summary>
    public static string NoTeamMessage(string departmentName) =>
        $"No team is currently assigned to {departmentName}. An administrator must assign it to a team (Teams, or Case Settings → Departments) before cases can be created for it.";

    /// <summary>The refusal for a department whose team is inactive.</summary>
    public static string InactiveTeamMessage(string departmentName, string teamName) =>
        $"{departmentName} is assigned to '{teamName}', which is inactive, so it cannot take new cases. Activate the team or assign the department to another team.";

    /// <summary>True when a save lost a race for the same department (the unique index refused the second mapping).</summary>
    public static bool IsDuplicateMapping(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == UniqueIndexName;

    /// <summary>The message for <see cref="IsDuplicateMapping"/>: someone else assigned it a moment ago.</summary>
    public const string RaceMessage = "One of these departments was just assigned to another team by someone else. Reload and choose again.";
}
