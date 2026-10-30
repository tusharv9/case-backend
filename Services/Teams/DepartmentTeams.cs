using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Api.Services.Teams;

/// <summary>
/// Which teams serve a business department, and where its cases go when no routing rule decides. A team serves a department
/// through a <c>TeamDepartments</c> link; only active teams count. The fallback is the department's default team, or the only
/// active team serving it; with several and no default there is no fallback, and a case that no rule routes is refused
/// rather than sent to an arbitrary team.
/// </summary>
public static class DepartmentTeams
{
    public sealed record Fallback(Guid? TeamId, string? Reason);

    public static async Task<Fallback> ResolveFallbackAsync(AppDbContext db, Guid orgDepartmentId, CancellationToken ct = default)
    {
        var serving = await db.TeamDepartments.AsNoTracking()
            .Where(l => l.OrgDepartmentId == orgDepartmentId && l.Team.IsActive)
            .Select(l => new { l.TeamId, l.IsDefault })
            .ToListAsync(ct);

        if (serving.FirstOrDefault(l => l.IsDefault) is { } preferred) return new Fallback(preferred.TeamId, null);
        if (serving.Count == 1) return new Fallback(serving[0].TeamId, null);
        return new Fallback(null, serving.Count == 0
            ? "no active team serves this department"
            : "several teams serve this department and none is set as its default team");
    }

    /// <summary>Whether the team serves the department. A case with no department (opened before departments) is served by any team.</summary>
    public static Task<bool> ServesAsync(AppDbContext db, Guid teamId, Guid? orgDepartmentId, CancellationToken ct = default) =>
        orgDepartmentId is not Guid dept
            ? Task.FromResult(true)
            : db.TeamDepartments.AsNoTracking().AnyAsync(l => l.TeamId == teamId && l.OrgDepartmentId == dept, ct);
}
