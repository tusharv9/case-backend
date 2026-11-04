using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// Test setup for fixtures that seed teams first: <see cref="LinkTeamsAsync"/> creates, for every team without departments, one
/// department of its own and maps it to that team, explicitly — the way an administrator would. Its id is
/// <see cref="IdFor"/>(team), so a fixture can file sub-categories under it before the department row exists.
/// </summary>
/// <remarks>
/// This is fixture data, not product behaviour: the application itself never creates a department for a team, nor a team for
/// a department.
/// </remarks>
public static class TestDepartments
{
    /// <summary>The id of the department <see cref="LinkTeamsAsync"/> creates for <paramref name="teamId"/>.</summary>
    public static Guid IdFor(Guid teamId)
    {
        var bytes = teamId.ToByteArray();
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5A;
        return new Guid(bytes);
    }

    public static async Task LinkTeamsAsync(AppDbContext ctx)
    {
        var teams = await ctx.Teams.ToListAsync();
        var linked = (await ctx.TeamDepartments.Select(l => l.TeamId).ToListAsync()).ToHashSet();
        foreach (var team in teams.Where(t => !linked.Contains(t.Id)))
        {
            var id = IdFor(team.Id);
            if (!await ctx.Departments.AnyAsync(d => d.Id == id))
                ctx.Departments.Add(new Department
                {
                    Id = id, Name = team.Name, Code = string.IsNullOrWhiteSpace(team.Code) ? team.Name : team.Code,
                    IsActive = true, CreatedAt = DateTime.UtcNow,
                });
            ctx.TeamDepartments.Add(new TeamDepartment { TeamId = team.Id, DepartmentId = id });
        }
        await ctx.SaveChangesAsync();

        var departmentOf = (await ctx.TeamDepartments.ToListAsync()).GroupBy(l => l.TeamId).ToDictionary(g => g.Key, g => g.First().DepartmentId);
        foreach (var c in await ctx.Cases.Where(c => c.DepartmentId == null).ToListAsync())
            c.DepartmentId = departmentOf.TryGetValue(c.TeamId, out var d) ? d : null;
        await ctx.SaveChangesAsync();
    }
}
